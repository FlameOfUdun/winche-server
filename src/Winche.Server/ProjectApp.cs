using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Npgsql;
using Winche.Assets;
using Winche.Assets.AspNetCore;
using Winche.Auth.AspNetCore;
using Winche.Auth.DependencyInjection;
using Winche.Console;
using Winche.Core;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Functions;
using Winche.Functions.Auth;
using Winche.Functions.Database;
using Winche.Functions.Storage;
using Winche.Storage.AspNetCore.DependencyInjection;
using Winche.Storage.AspNetCore.Rest.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.FileSystem;
using Winche.Storage.S3.DependencyInjection;
using Winche.Webhooks;

namespace Winche.Server;

/// <summary>
/// One project on this server: its own PostgreSQL database, files folder and public address, at <c>/p/&lt;id&gt;</c>.
/// </summary>
/// <param name="ConnectionString">Its data database: documents, files' records, app users, hook deliveries, logs.</param>
/// <param name="ConfigConnectionString">Its configuration: its own schema in the server's control database (settings,
/// secrets, rules, apps, members, tokens, functions).</param>
public sealed record ServerProject(string Id, string ConnectionString, string FilesPath, string PublicUrl, string ConfigConnectionString);

/// <summary>
/// The whole Winche stack for one project, as its own app (its own services, settings, keys, background work) run
/// in this process behind the server's router (<see cref="ProjectRouter"/>).
/// </summary>
/// <summary>
/// One key ring for the whole server (its console and every project's), so a console sign-in made in one is valid in
/// every other. Encrypted at rest with a key derived from the master key (Winche.Core); older plain keys are encrypted too.
/// </summary>
internal static class ServerKeyRing
{
    public static void Add(IServiceCollection services, ProjectHosts projects) =>
        services.AddDataProtection().SetApplicationName("winche-server")
            .Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>(sp =>
                new Microsoft.Extensions.Options.ConfigureOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
                {
                    var keys = sp.GetRequiredService<WincheKeys>();
                    var encryptor = new MasterKeyXmlEncryptor(keys);
                    o.XmlEncryptor = encryptor;
                    o.XmlRepository = new DataProtectionKeys(projects.ControlDb, encryptor, keys);
                }));

    /// <summary>Console emails (invites, password resets): through the console accounts' email (Server settings → Authentication → Settings → Email).</summary>
    public static Winche.Console.Email.IConsoleEmailSender ConsoleEmail(ProjectHosts projects)
    {
        // Only a real sender counts (an SMTP server, or the server's own email): links that are just logged would leave
        // invitees waiting for an email.
        var auth = projects.Accounts.Services.GetRequiredService<Winche.Auth.WincheAuthOptions>();
        var host = projects.Accounts.Services.GetService<Winche.Auth.Email.IAuthEmailSender>() is Winche.Console.Email.IHostEmailSender;
        return new Winche.Console.Email.WincheAuthConsoleEmail(() => auth.EmailSending is { } e && (e.Smtp is not null || !e.LogLinksOnly && host) ? e : null,
            projects.Accounts.Services, "Server settings → Authentication → Settings → Email");
    }
}

internal static class ProjectApp
{
    public static async Task<(WebApplication App, InProcessServer Server)> BuildAsync(ServerOptions server, ServerSettingsProvider settings,
        ServerProject project, ProjectHosts projects, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment.EnvironmentName,
            ContentRootPath = environment.ContentRootPath,
            ApplicationName = environment.ApplicationName,
        });
        builder.Configuration.AddConfiguration(configuration);   // logging levels and the like, as the server has them
        var inProcess = new InProcessServer();
        builder.Services.AddSingleton<IServer>(inProcess);
        builder.Services.AddWincheProductSetupOnDemand();   // each product's tables only once it's started in the console

        var conn = project.ConnectionString;                 // the project's data
        var config = project.ConfigConnectionString;         // the project's configuration (its schema in the control database)
        var serverConn = server.ConnectionString;   // what every project shares: data-protection keys, console accounts
        var publicUrl = project.PublicUrl;

        builder.Services.AddSingleton(server);
        builder.Services.AddSingleton(project);
        // The console's project switcher: every project on this server, and creating one.
        builder.Services.AddSingleton<Winche.Console.Project.IWincheProjects>(new ServerProjectsForConsole(projects, project, server));
        ServerKeyRing.Add(builder.Services, projects);

        // Settings the console changes, and its encrypted secrets.
        builder.Services.AddWincheConfig(o => { o.ConnectionString = config; o.MasterKey = server.MasterKey; o.PreviousMasterKeys = server.PreviousMasterKeys; });
        Winche.Secrets.Aws.SecretsAwsServiceCollectionExtensions.AddWincheSecretsAws(builder.Services);   // secrets may be references to AWS Secrets Manager or Parameter Store

        // Users: Winche Auth (email and password and two-step sign-in to start with; everything else in the console).
        builder.Services.AddWincheAuth(o =>
        {
            o.ConnectionString = conn;
            o.Issuer = publicUrl;
            o.EmailPassword();
            o.Mfa();
            o.UseSettings();
        });
        // The server's own email, when the deployment has one: sign-in emails go through it unless the project chooses SMTP.
        if (ServerEmail.Of(server) is { } serverEmail)
            builder.Services.AddSingleton<Winche.Auth.Email.IAuthEmailSender>(sp =>
                serverEmail.For(() => sp.GetRequiredService<Winche.Auth.WincheAuthOptions>().EmailSending?.AppName ?? project.Id));
        builder.Services.AddAuthentication().AddWincheTokens(t => t.TrustWincheAuth().UseSettings());

        // Who is calling: the verified token's claims, once per request, for every package (rules, logs, functions' ctx.auth).
        builder.Services.AddWincheCaller(WincheClaims.FromAsync);

        // Webhooks for documents, files and users (anything else is a function with a trigger).
        builder.Services.AddWincheHooks(o => o.ConnectionString = conn);

        // Functions: TypeScript run by Deno in sandboxed workers, deployed with the CLI.
        builder.Services.AddWincheFunctions(o =>
        {
            o.ConnectionString = config;   // deployments and schedules are configuration
            o.DenoPath = string.IsNullOrWhiteSpace(server.DenoPath) ? null : server.DenoPath;   // the image has deno on the PATH
            FunctionsLongestRun.Apply(o, settings.Current);   // the server's limits; followed below
            o.UseSettings();   // Functions → Settings: the project's default and each function's own time
        });
        builder.Services.AddHostedService(sp => new FunctionsLongestRun(settings, sp.GetRequiredService<Winche.Functions.FunctionsOptions>()));
        builder.Services.AddWincheFunctionsForDatabase().AddWincheFunctionsForStorage().AddWincheFunctionsForAuth();
        Winche.Functions.Assets.AssetsFunctionsExtensions.AddWincheFunctionsForAssets(builder.Services);   // ctx.assets, assets triggers

        // Documents: REST and live (WebSocket) APIs; rules start as "deny everything" until published in the console.
        builder.Services.AddWincheDatabase(o =>
        {
            o.ConnectionString = conn;
            o.LogRequests();
            o.UseSettings();
            o.UseHooks(h => h.PublishChanges());
        }).AddWincheDatabaseWsApi(ws => { ws.UseSettings(); ws.Compression = true; });
        // Disabled or deleted app users lose their open database connections at once.
        builder.Services.AddSingleton<Winche.Auth.IWincheAuthObserver, ClosedSockets>();

        // Files: on disk under the project's folder, served through signed links; the console can move them to a bucket.
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = conn;
            o.LogRequests();
            o.UseSettings();
            o.AllowS3Locations();
            o.UseHooks(h => h.PublishChanges());
            o.UseFileSystemArchive(fs =>
            {
                fs.Root = project.FilesPath;
                fs.PublicBaseUrl = publicUrl;
            });
        });

        // Assets: versioned content apps read (translations, pages, media), published from the CLI; kept in the project's
        // files folder unless the console moves them to a bucket. Rules start as "deny everything" until published.
        builder.Services.AddWincheAssets(o =>
        {
            o.ConnectionString = conn;
            o.UseSettings();
        });

        // The console, at ConsolePath under the project's path. Accounts are the server's: one sign-in, every project.
        builder.Services.AddWincheConsole(o =>
        {
            o.ConnectionString = config;          // rules versions
            o.LogsConnectionString = conn;        // the Logs tab's entries are data
            o.AccountsConnectionString = serverConn;
            o.Accounts = projects.Accounts;   // the console's accounts: one Winche Auth for the whole server
            o.SignInCookiePath = "/";
            o.AddDatabaseTab(b => { b.UseRulesEditor(); b.UseLogs(); b.UseHistory(); });
            o.AddStorageTab(b => { b.UseRulesEditor(); b.UseLogs(); });
            o.AddAssetsTab(b => b.UseRulesEditor());
            o.AddAuthenticationTab();
            o.AddLogsTab();
            // Console emails: the server's own SMTP server (Server settings), never this project's.
            // Console emails (invites, password resets): through the console accounts' email, as Server settings →
            // Authentication → Settings → Emails to users sets it (its SMTP server and password).
            // Only a real sender counts (an SMTP server, or the server's own email): links that are just logged would
            // leave invitees waiting for an email.
            o.UseEmailSender(_ => ServerKeyRing.ConsoleEmail(projects));
            o.RequireTwoFactorForSettings = settings.Current.RequireTwoFactorForSettings;
            // The console's cookie outlives use: the accounts' session (renewed every few minutes) decides how long it lasts.
            o.SessionLifetime = TimeSpan.FromDays(400);
            o.ServerSettings = new ServerSettingsForConsole(settings, projects);
            o.DataLocation = new ServerDataLocationForConsole(projects, ProjectHosts.KeyOf(project));   // Project settings → Data   // Server settings, for server admins
            o.ProjectId = project.Id;
            o.UseProjectSetup(carryOver: false);   // made by the server: shares accounts with others, never "in use" before
            o.UseProjectMembers();   // who may use it is its own list; server admins use every project
        });

        builder.Services.AddCors();
        // Compressed answers (Brotli, else gzip) for the apps' APIs and the console's files: see UseCompression below.
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            o.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(["application/javascript", "image/svg+xml"]);
        });
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        // A request whose body can't be read (bad JSON, a missing or unknown field, nested too deep) throws, so the
        // middleware below answers it in the Winche error shape with the reason, rather than an empty 400.
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            try { await next(http); }
            catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
            {
                // The JSON reader's own words say what's wrong (the outer message only names the parameter).
                var why = ex.InnerException is System.Text.Json.JsonException { Message: { Length: > 0 } inner } ? Plain(inner) : ex.Message;
                await new Winche.WincheError(Winche.WincheErrorCode.InvalidArgument, $"The request body isn't right: {why}", "bad-request")
                    .ToResult().ExecuteAsync(http);
            }
        });
        // CORS: the project's browser origins and its registered web apps' (Project settings → General), read as
        // requests come, so changes apply at once. None set, or *: any origin.
        var registeredApps = app.Services.GetRequiredService<Winche.Console.Apps.RegisteredApps>();
        app.UseCors(p =>
        {
            p.SetIsOriginAllowed(origin => registeredApps.Current.AllowsOrigin(origin));
            p.AllowAnyHeader().AllowAnyMethod();
        });
        // Compression only where another site can't make the browser send the credentials: the apps' APIs (a bearer token
        // in a header) and the console's static files. Never the console's own API or sign-in (cookies), where a
        // compressed answer's size could give away what it holds (BREACH).
        app.UseWhen(http => Compressible(http.Request.Path, server.ConsolePath), branch => branch.UseResponseCompression());
        app.UseWebSockets();
        app.UseWincheWsQueryToken();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWincheCaller();
        Winche.Console.Project.ProjectEndpoints.UseWincheProjectGate(app);   // a product's API answers once it's started

        // Products are set up when they're started (and again as the project starts): nothing is made for those that aren't.
        app.MapWincheDatabaseRestApi();
        app.MapWincheDatabaseWsApi();
        app.MapWincheStorageRestApi();
        app.MapWincheStorageFileSystem();
        app.MapWincheAuth();
        app.MapWincheFunctions();
        app.MapWincheAssets();
        app.MapWincheConsole(server.ConsolePath);
        Winche.Console.Apps.AppSetupEndpoints.MapWincheAppSetup(app);   // GET /.well-known/winche: winche configure reads it
        app.MapGet("/", (HttpContext http) => Results.Redirect($"{http.Request.PathBase}{server.ConsolePath}"));

        await app.StartAsync(ct);
        return (app, inProcess);
    }

    /// <summary>The JSON reader's message in plain words, without .NET type names.</summary>
    /// <summary>Whether answers on <paramref name="path"/> may be compressed (see the pipeline).</summary>
    internal static bool Compressible(PathString path, string consolePath) =>
        Api(path, "/documents/v1") || Api(path, "/files/v1") || Api(path, "/functions/v1") || Api(path, "/assets/v1")
        || (path.StartsWithSegments(consolePath, out var rest) && !rest.StartsWithSegments("/api"));

    /// <summary>An API's routes: <c>/documents/v1</c>, <c>/documents/v1/…</c> and its verbs, <c>/documents/v1:runQuery</c>.</summary>
    private static bool Api(PathString path, string prefix) =>
        path.StartsWithSegments(prefix) || (path.Value?.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static string Plain(string message)
    {
        // "JSON deserialization for type 'X' was missing required properties including: 'fields'."
        var missing = System.Text.RegularExpressions.Regex.Match(message, @"missing required properties including: (.+?)\.?$");
        if (missing.Success) return $"it's missing {missing.Groups[1].Value}.";
        // "The JSON value could not be converted to System.Int64. Path: $.n | LineNumber: …"
        var convert = System.Text.RegularExpressions.Regex.Match(message, @"could not be converted to [^.]+(?:\.[^.\s]+)*\. Path: (\$[^ |]*)");
        if (convert.Success) return $"the value at {convert.Groups[1].Value} isn't of the right kind.";
        return message;
    }
}
