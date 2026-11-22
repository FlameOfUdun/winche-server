using Microsoft.AspNetCore.Hosting.Server;
using Winche.Console;
using Winche.Core;

namespace Winche.Server;

/// <summary>
/// The server's own app, at its root: its console (first-run setup, sign-in, the accounts, Server settings and the
/// projects) and nothing of a project's. The server runs with no project at all; each project is at
/// <c>/p/&lt;id&gt;</c>, with its own console and APIs. App APIs asked for at the root are told where to go.
/// </summary>
public sealed class ServerConsoleApp(ServerOptions server, ServerSettingsProvider settings, ProjectHosts projects, IConfiguration configuration,
    IWebHostEnvironment environment) : IHostedService
{
    private (WebApplication App, InProcessServer Server)? _current;
    private ServerSettings? _applied;

    /// <summary>Where the router hands requests: the console app running now.</summary>
    internal InProcessServer Server => _current?.Server ?? throw new InvalidOperationException("The server's console isn't running yet.");

    /// <summary>For tests and diagnostics: the console app's services.</summary>
    internal IServiceProvider Services => _current?.App.Services ?? throw new InvalidOperationException("The server's console isn't running yet.");

    public async Task StartAsync(CancellationToken ct)
    {
        _current = await BuildAsync(ct);
        _applied = settings.Current;
        // Settings it's built with changed (two-step for settings, the address): a new one takes over, the old one stops.
        settings.Area.Changed += next =>
        {
            var before = _applied!;
            _applied = next;
            if (!ProjectHosts.NeedsRestart(before, next)) return;
            _ = Task.Run(async () =>
            {
                var old = _current;
                _current = await BuildAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (old is { } o) { await o.App.StopAsync(); await o.App.DisposeAsync(); }
            });
        };
    }

    private async Task<(WebApplication, InProcessServer)> BuildAsync(CancellationToken ct)
    {
        var inProcess = new InProcessServer();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment.EnvironmentName,
            ContentRootPath = environment.ContentRootPath,
            ApplicationName = environment.ApplicationName,
        });
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddSingleton<IServer>(inProcess);
        builder.Services.AddSingleton(server);
        ServerKeyRing.Add(builder.Services, projects);
        // Its own configuration (the CLI's sign-in codes, its tokens): the server's schema of the control database.
        builder.Services.AddWincheConfig(o =>
        {
            o.ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = "server" }.ConnectionString;
            o.MasterKey = server.MasterKey;
            o.PreviousMasterKeys = server.PreviousMasterKeys;
        });
        Winche.Secrets.Aws.SecretsAwsServiceCollectionExtensions.AddWincheSecretsAws(builder.Services, o => o.ServiceUrl = server.SecretsAwsServiceUrl);   // secrets may be references to AWS Secrets Manager or Parameter Store
        // The server's own secrets Winche keeps in AWS: under <prefix>/_server/ (no project id has a _).
        Winche.Secrets.SecretsServiceCollectionExtensions.AddWincheSecrets(builder.Services, o => o.ManagedPrefix = $"{server.SecretsPrefix.Trim('/')}/_server/");
        builder.Services.AddSingleton<Winche.Console.Project.IWincheProjects>(new ServerProjectsForConsole(projects, null, server));
        // Server settings → Authentication runs on the accounts' own services (each request gets them first): these
        // say they exist here too, so the console's endpoints for them map.
        // (Only types the accounts always have: a lookup that finds nothing must stay null, not land here.)
        foreach (var type in new[] { typeof(Winche.Auth.IWincheAuth), typeof(Winche.Auth.Tokens.SigningKeyStore) })
            builder.Services.AddScoped(type, _ => throw new InvalidOperationException($"{type.Name} comes from the console accounts' services."));
        builder.Services.AddWincheConsole(o =>
        {
            o.ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = "server" }.ConnectionString;
            o.AccountsConnectionString = server.ConnectionString;
            o.Accounts = projects.Accounts;   // one set of accounts for the server and every project
            o.SignInCookiePath = ServerOptions.ConsolePath;   // the console's cookie never goes to the apps' addresses
            o.UseServerConsole();
            o.UseEmailSender(_ => ServerKeyRing.ConsoleEmail(projects));
            o.RequireTwoFactorForSettings = settings.Current.RequireTwoFactorForSettings;
            o.SessionLifetime = TimeSpan.FromDays(400);
            o.ServerSettings = new ServerSettingsForConsole(settings, projects);
            o.SetupCode = settings.SetupCodeAsync;   // first-run setup asks for it (the server's log has it)
        });
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        // The console's page and files compressed, as a project's are (never its API: cookies, see ProjectApp).
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            o.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(["application/javascript", "image/svg+xml"]);
        });
        builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

        var app = builder.Build();
        app.UseWhen(http => ProjectApp.ConsoleCompressible(http.Request.Path), branch => branch.UseResponseCompression());
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWincheConsole(ServerOptions.ConsolePath);
        app.MapGet("/", () => Results.Redirect(ServerOptions.ConsolePath));
        // Anything else at the root (an app's API without its project): where projects are.
        app.MapFallback((HttpContext http) => new Winche.WincheError(Winche.WincheErrorCode.NotFound,
            $"There's nothing at {http.Request.Path} on this server: a project's APIs are at {ServerOptions.ProjectsPrefix}/<project id>{http.Request.Path}.", "no-project").ToResult());
        await app.StartAsync(ct);
        return (app, inProcess);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_current is not { } current) return;
        await current.App.StopAsync(ct);
        await current.App.DisposeAsync();
    }
}
