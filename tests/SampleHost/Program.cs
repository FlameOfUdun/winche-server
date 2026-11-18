using Winche.Console.Apps;
using SampleHost;
using Winche.Auth.AspNetCore;
using Winche.Auth.DependencyInjection;
using Winche.Core;
using Winche.Console;
using Winche.Console.Tabs;
using Winche.Database.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;

var builder = WebApplication.CreateBuilder(args);
var wincheConn = builder.Configuration["Winche:ConnectionString"]
    ?? throw new InvalidOperationException("Winche:ConnectionString is required.");

builder.Services.AddWincheDatabase(cfg =>
{
    cfg.ConnectionString = wincheConn;
    cfg.KeepHistory();   // the Database tab's History sub-tab
    cfg.LogRequests(r => r.SuccessSampleRate = 1.0);   // the Logs Explorer: every request
    cfg.UseIndexes(ib => ib.Add("patients", [new("ward")]));
    cfg.UseSettings();   // Database → Settings
    cfg.UseHooks(h => h.PublishChanges());   // document changes for the Hooks page
});
Winche.Webhooks.ServiceCollectionExtensions.AddWincheHooks(builder.Services, o => o.ConnectionString = wincheConn);
// Functions (Deno from WINCHE_DENO, else PATH): the Functions page.
Winche.Functions.ServiceCollectionExtensions.AddWincheFunctions(builder.Services, o =>
{
    o.ConnectionString = wincheConn;
    o.DenoPath = Environment.GetEnvironmentVariable("WINCHE_DENO") is { Length: > 0 } deno ? deno : null;
    // The TypeScript SDKs the tests carry (tests/SdkPackages), for code that doesn't name them in deno.json.
    foreach (var name in new[] { "winche-functions", "winche-admin" })
        if (Path.Combine(AppContext.BaseDirectory, "SdkPackages", "@winche-technologies", name) is var dir && Directory.Exists(dir)) o.ProvidedPackages[$"@winche-technologies/{name}"] = dir;
});
Winche.Functions.Database.DatabaseFunctionsExtensions.AddWincheFunctionsForDatabase(builder.Services);
builder.Services.AddWincheStorage(opts =>
{
    opts.ConnectionString = wincheConn;
    opts.MaxFileSizeBytes = 10 * 1048576;
    opts.UseSettings();   // Storage → Settings
    opts.UseHooks(h => h.PublishChanges());
});
builder.Services.AddSingleton<IArchive, NoOpArchive>();
// Assets (the Assets page, the apps' /assets/v1, functions' ctx.assets), their contents in memory; apps read
// everything under public/ and, signed in, everything else.
Winche.Assets.AssetsServiceCollectionExtensions.AddWincheAssets(builder.Services, o =>
{
    o.ConnectionString = wincheConn;
    o.UseArchive(_ => new MemoryContentArchive());
    o.UseRulesText("match public/{rest=**} { allow read; } match {path=**} { allow read: if request.auth != null; }");
    o.UseSettings();   // Assets → Settings
});
Winche.Functions.Assets.AssetsFunctionsExtensions.AddWincheFunctionsForAssets(builder.Services);

{
    var authConn = builder.Configuration["Console:ConnectionString"]
        ?? throw new InvalidOperationException("Console:ConnectionString is required.");

    // The app's own users (Winche Auth), managed in the console's Authentication tab — unless the test runs the
    // Clinic shape: only an external provider (Sample:ExternalOnly), managed in the tab's Settings.
    var externalOnly = string.Equals(builder.Configuration["Sample:ExternalOnly"], "true", StringComparison.OrdinalIgnoreCase);
    if (!externalOnly) builder.Services.AddWincheAuth(a =>
    {
        a.ConnectionString = wincheConn;
        a.Issuer = "https://sample.test";
        a.EmailPassword();
        a.Anonymous();
        a.Email(e => e.AppName = "Sample");
        a.Mfa();
        a.Google(g => { g.Audiences = ["sample.apps.googleusercontent.com"]; g.AllowUnsignedTokensForTesting = true; });
        // A Keycloak-synced provider (Sample:KeycloakSync). Tests leave its Keycloak unreachable, so the sync fails; set
        // Sample:KeycloakAuthority and Sample:KeycloakSyncSecret to sync from a real one.
        if (string.Equals(builder.Configuration["Sample:KeycloakSync"], "true", StringComparison.OrdinalIgnoreCase))
            a.OpenIdConnect("staff", k =>
            {
                k.DisplayName = "Staff Keycloak";
                k.Authority = builder.Configuration["Sample:KeycloakAuthority"] ?? "http://127.0.0.1:9/realms/staff";
                k.Audiences = ["staff-app"];
                k.AllowUnsignedTokensForTesting = true;
                k.SyncFromKeycloak(s =>
                {
                    s.ClientId = "winche-sync";
                    s.ClientSecret = builder.Configuration["Sample:KeycloakSyncSecret"] ?? "secret";
                    s.WebhookSecret = "0123456789abcdef0123";
                    s.PollInterval = builder.Configuration["Sample:KeycloakAuthority"] is null ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(10);
                    s.MapAttribute("ward");
                });
            });
        a.UseSettings();
    });
    builder.Services.AddSingleton<RecordingEmailSender>();
    builder.Services.AddSingleton<Winche.Auth.Email.IAuthEmailSender>(sp => sp.GetRequiredService<RecordingEmailSender>());
    builder.Services.AddWincheConfig(c =>   // settings the console can change, and its encrypted secrets
    {
        c.ConnectionString = wincheConn;
        c.MasterKey = "sample-host-master-key-for-tests-only-0123456789";
        c.PollInterval = TimeSpan.FromMilliseconds(500);
    });
    Winche.Secrets.SecretsServiceCollectionExtensions.AddWincheSecrets(builder.Services);
    builder.Services.AddAuthentication().AddWincheTokens(t => (externalOnly ? t : t.TrustWincheAuth()).UseSettings());
    // The console's accounts: a Winche Auth of their own (Winche Server runs one for the whole server), in their own schema.
    var accountsConn = new Npgsql.NpgsqlConnectionStringBuilder(authConn) { SearchPath = "console_accounts" }.ConnectionString;
    var authDb = new Npgsql.NpgsqlConnectionStringBuilder(authConn).Database!;
    await using (var server = Npgsql.NpgsqlDataSource.Create(new Npgsql.NpgsqlConnectionStringBuilder(authConn) { Database = "postgres" }.ConnectionString))
    {
        await using var exists = server.CreateCommand("SELECT 1 FROM pg_database WHERE datname = $1");
        exists.Parameters.AddWithValue(authDb);
        if (await exists.ExecuteScalarAsync() is null)
            try { await using var create = server.CreateCommand($"CREATE DATABASE \"{authDb}\""); await create.ExecuteNonQueryAsync(); }
            catch (Npgsql.PostgresException e) when (e.SqlState == "42P04") { }   // made meanwhile
    }
    await using (var db = Npgsql.NpgsqlDataSource.Create(authConn))
    await using (var cmd = db.CreateCommand("CREATE SCHEMA IF NOT EXISTS console_accounts"))
        await cmd.ExecuteNonQueryAsync();
    // Started with this host (not before: the test host takes the first app built in the process as the one to test).
    var accounts = new SampleAccounts(accountsConn);
    builder.Services.AddHostedService(_ => accounts);
    builder.Services.AddWincheConsole(o =>
    {
        o.ConnectionString = authConn;   // no seed: tests use /setup
        o.Accounts = accounts;
        o.AddAuthenticationTab();
        // Guided setup (Sample:ProjectSetup): create the project, start products one by one.
        if (string.Equals(builder.Configuration["Sample:ProjectSetup"], "true", StringComparison.OrdinalIgnoreCase)) o.UseProjectSetup();
        // Tests turn this on to check it; the rest change settings without enrolling an authenticator.
        o.RequireTwoFactorForSettings = string.Equals(builder.Configuration["Sample:RequireTwoFactor"], "true", StringComparison.OrdinalIgnoreCase);

        o.AddDatabaseTab(b => { b.UseRulesEditor(); b.UseLogs(); b.UseHistory(); });   // Database tab + Rules, Logs, History
        o.AddStorageTab(b => { b.UseRulesEditor(); b.UseLogs(); });     // Storage tab + its Rules and Logs sub-tabs
        o.AddAssetsTab(b => b.UseRulesEditor());                         // Assets: releases, channels, rules, settings
        o.AddLogsTab(l =>   // the app's logs (tests: written and synced quickly)
        {
            l.FlushInterval = TimeSpan.FromMilliseconds(100);
            l.LevelSyncInterval = TimeSpan.FromMilliseconds(200);
        });
    });
}

var app = builder.Build();

await app.InitializeWincheDatabaseAsync();
await app.InitializeWincheStorageAsync();
Winche.Console.Project.ProjectEndpoints.UseWincheProjectGate(app);   // products not started refuse their APIs (with UseProjectSetup)

if (app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(Winche.Auth.IWincheAuth)) == true)
    app.MapWincheAuth();   // the app's sign-in API (/auth/v1)

Winche.Functions.FunctionsEndpoints.MapWincheFunctions(app);   // callables and CLI deploys
app.MapWincheConsole("/console");
Winche.Assets.AspNetCore.AssetsEndpoints.MapWincheAssets(app);   // apps read channels and files at /assets/v1
app.MapWincheAppSetup();

app.Run();

public partial class Program { }

/// <summary>Keeps Winche Auth's messages for the tests to read.</summary>
public sealed class RecordingEmailSender : Winche.Auth.Email.IAuthEmailSender
{
    public List<Winche.Auth.Email.AuthEmailMessage> Sent { get; } = [];
    public Task SendAsync(Winche.Auth.Email.AuthEmailMessage message, CancellationToken ct)
    {
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>The console accounts' app, started and stopped with this host.</summary>
internal sealed class SampleAccounts(string connectionString) : Winche.Console.Identity.IConsoleAccountsHost, Microsoft.Extensions.Hosting.IHostedService
{
    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("The console accounts haven't started.");

    public async Task StartAsync(CancellationToken ct) =>
        _app = await Winche.Console.Identity.ConsoleAccountsApp.StartAsync(connectionString, "sample-host-master-key-for-tests-only-0123456789", [],
            "https://sample.test/console/accounts", "https://sample.test/console/reset-password", ct: ct);

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        await _app.StopAsync(ct);
        await _app.DisposeAsync();
    }
}
