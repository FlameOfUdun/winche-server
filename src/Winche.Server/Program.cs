using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Winche.Core;
using Winche.Server;

// Winche Server: the whole stack, ready to run, for one or many projects. Only what it needs to start comes from
// configuration (section "Winche", e.g. Winche__ConnectionString); rules, sign-in, providers, settings and hooks are set
// up in each project's console.
//
// Each project runs as its own app in this process (ProjectApp: its own database, services, keys and background work);
// this app is only the router in front of them (ProjectRouter): /p/<id>/… goes to that project, anything else to the
// default project — the server's own database, as before there were projects.
var builder = WebApplication.CreateBuilder(args);
var server = builder.Configuration.GetSection("Winche").Get<ServerOptions>() ?? new ServerOptions();
if (server.Problems() is { Count: > 0 } problems)
{
    Console.Error.WriteLine("Winche Server can't start. Set:");
    foreach (var p in problems) Console.Error.WriteLine($"  - {p}");
    return 2;
}

builder.Services.AddSingleton(server);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind a proxy or load balancer (the usual way to run it): trust its X-Forwarded-* headers.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
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

var app = builder.Build();
app.UseForwardedHeaders();
var projects = app.Services.GetRequiredService<ProjectHosts>();
app.Run(async http =>
{
    // For load balancers and orchestrators: 200 once the database answers.
    if (http.Request.Path == "/healthz")
    {
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
    await ProjectRouter.RouteAsync(http, projects);
});

await app.RunAsync();
return 0;

/// <summary>For tests (WebApplicationFactory).</summary>
public partial class Program;
