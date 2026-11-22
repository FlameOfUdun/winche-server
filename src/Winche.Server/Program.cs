using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Winche.Core;
using Winche.Server;

// Winche Server: the whole stack, ready to run, for one or many projects. Only what it needs to start comes from
// configuration (section "Winche", e.g. Winche__ConnectionString); rules, sign-in, providers, settings and hooks are set
// up in each project's console.
//
// Each project runs as its own app in this process (ProjectApp: its own database, services, keys and background work);
// the server's own console is an app too (ServerConsoleApp: setup, sign-in, accounts, Server settings, the projects), and
// it runs with no project at all. This app is only the router in front of them (ProjectRouter): /p/<id>/… goes to that
// project, anything else to the server's console.
var builder = WebApplication.CreateBuilder(args);
var server = builder.Configuration.GetSection("Winche").Get<ServerOptions>() ?? new ServerOptions();
if (server.Problems() is { Count: > 0 } problems)
{
    Console.Error.WriteLine("Winche Server can't start. Set:");
    foreach (var p in problems) Console.Error.WriteLine($"  - {p}");
    return 2;
}

builder.Services.AddSingleton(server);
// A stop (SIGTERM) waits this long for requests and function runs going on.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(server.StopTimeoutSeconds));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind a proxy or load balancer (the usual way to run it): trust its X-Forwarded-* headers, and only its. By
    // default proxies on loopback and private networks; Winche__TrustedProxies__0=… names others, "*" any.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    if (server.ProxyNetworks() is not { } networks) return;   // any (empty lists: ASP.NET Core then believes every sender)
    foreach (var n in networks) o.KnownIPNetworks.Add(n);
});
// The server's own settings (Server settings in the console, for server admins): in the control database's "server"
// schema, starting from what the deployment sets. The schema is made first.
builder.Services.AddHostedService<ControlSchemaSetup>();
builder.Services.AddWincheConfig(o =>
{
    o.ConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = "server" }.ConnectionString;
    o.MasterKey = server.MasterKey;
    o.PreviousMasterKeys = server.PreviousMasterKeys;
});
Winche.Secrets.Aws.SecretsAwsServiceCollectionExtensions.AddWincheSecretsAws(builder.Services);   // secrets may be references to AWS Secrets Manager or Parameter Store
var deployed = ServerSettings.From(server);
// The public address is the deployment's (Winche__PublicUrl): where DNS and the proxy put the server, and the issuer of
// its tokens. The console shows it but can't change it.
Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(builder.Services, ServerSettings.Area, "Server", deployed, s =>
    s.PublicUrl.TrimEnd('/') == deployed.PublicUrl ? s.Problems()
        : [.. s.Problems(), "The public address is set by the deployment (Winche__PublicUrl): change it there."]);
builder.Services.AddSingleton<ServerSettingsProvider>();
builder.Services.AddHostedService<ConsoleSignInMove>();
// The console's accounts: a Winche Auth of their own, started before the projects whose consoles use it.
builder.Services.AddSingleton<ConsoleAccounts>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleAccounts>());
builder.Services.AddSingleton<ProjectHosts>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectHosts>());   // starts every project before requests come in
builder.Services.AddSingleton<ServerConsoleApp>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerConsoleApp>());

// The folders it writes to must be writable, or projects fail to start later with errors that don't say why.
if (Winche.Console.Api.ServerFolders.Problems([server.ProjectsPath, Environment.GetEnvironmentVariable("DENO_DIR") ?? ""]) is { Count: > 0 } unwritable)
{
    foreach (var problem in unwritable) Console.Error.WriteLine($"Winche Server can't start: {problem}");
    Environment.Exit(1);
}

var app = builder.Build();
app.UseForwardedHeaders();
var projects = app.Services.GetRequiredService<ProjectHosts>();
var console = app.Services.GetRequiredService<ServerConsoleApp>();
var ready = false;      // projects started and warm: their first callers won't wait
var stopping = false;   // a stop began: load balancers should send nothing new
app.Lifetime.ApplicationStopping.Register(() => stopping = true);
app.Run(async http =>
{
    // For the container's own check: the process runs.
    if (http.Request.Path == "/livez")
    {
        await Results.Ok(new { status = "ok" }).ExecuteAsync(http);
        return;
    }
    // For load balancers: 200 once projects are started and warm (functions' kept instances loaded) and the database
    // answers; 503 while starting or stopping.
    if (http.Request.Path == "/healthz")
    {
        if (stopping || !ready)
        {
            await Results.Json(new { status = stopping ? "stopping" : "starting" }, statusCode: 503).ExecuteAsync(http);
            return;
        }
        try
        {
            await using var db = new NpgsqlConnection(server.ConnectionString);
            await db.OpenAsync(http.RequestAborted);
            await Results.Ok(new { status = "ok" }).ExecuteAsync(http);
        }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            await Results.Json(new { status = "unavailable", reason = ex.GetBaseException().Message }, statusCode: 503).ExecuteAsync(http);
        }
        return;
    }
    await ProjectRouter.RouteAsync(http, projects, console);
});

// Warm-up: once the server listens, each project answers one request of its own (a process's first request compiles
// code and opens database connections) and its functions' kept instances load, so the first caller after a start or
// restart isn't the one who waits; /healthz says ready after. Best effort (a minute at most); skipped where there's no
// network address (tests).
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is null || !Uri.TryCreate(address.Replace("://+", "://localhost").Replace("://*", "://localhost")
                .Replace("://[::]", "://localhost").Replace("://0.0.0.0", "://localhost"), UriKind.Absolute, out var local)) return;
        using var http = new HttpClient { BaseAddress = local, Timeout = TimeSpan.FromSeconds(30) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var host in projects.All)
        {
            try
            {
                (await http.GetAsync($"{ServerOptions.ProjectsPrefix}/{host.Project.Id}/.well-known/winche")).Dispose();
                if (host.App.Services.GetService<Winche.Functions.WincheFunctions>() is { } functions)
                {
                    await functions.RefreshAsync();
                    await functions.Warmed.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, 60 - watch.Elapsed.TotalSeconds)));
                }
            }
            catch (Exception ex) { app.Logger.LogDebug(ex, "Warming project {Project} up didn't work; its first request does it.", host.Project.Id); }
        }
        app.Logger.LogInformation("Ready: {Projects} projects warm in {Ms} ms.", projects.All.Count, (int)watch.ElapsedMilliseconds);
    }
    finally { ready = true; }
}));

await app.RunAsync();
return 0;

/// <summary>For tests (WebApplicationFactory).</summary>
public partial class Program;
