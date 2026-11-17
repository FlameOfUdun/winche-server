using Npgsql;

namespace Winche.Server;

/// <summary>
/// The console's accounts: a Winche Auth of their own, for the people who run the server (not any project's app users).
/// It has its own users, sessions, signing keys, settings (sign-in methods, providers, two-step, email, templates) and
/// secrets, in the control database's <see cref="Schema"/> schema, and runs as its own app inside the server, with no
/// address of its own: the consoles reach it through <see cref="Services"/>.
/// </summary>
public sealed class ConsoleAccounts(ServerOptions server, ServerSettingsProvider settings, IConfiguration configuration,
    ILogger<ConsoleAccounts> logger) : IHostedService, Winche.Console.Identity.IConsoleAccountsHost
{
    /// <summary>Where the console accounts' tables live (not project_…: a project could be called "console").</summary>
    public const string Schema = "console_accounts";

    private WebApplication? _app;

    /// <summary>Its services: <see cref="IWincheAuth"/> (in a scope), its settings and secrets.</summary>
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("The console accounts aren't running yet.");

    /// <summary>The console accounts' connection: the control database, searching their own schema.</summary>
    internal string ConnectionString => new NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = Schema }.ConnectionString;

    /// <summary>The issuer of its tokens: the console's own, under the server's address (they never leave the server).</summary>
    internal string Issuer => settings.Current.PublicUrl.TrimEnd('/') + ServerOptions.ConsolePath + "/accounts";

    public async Task StartAsync(CancellationToken ct)
    {
        await using (var control = NpgsqlDataSource.Create(server.ConnectionString))
        await using (var cmd = control.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{Schema}\""))
            await cmd.ExecuteNonQueryAsync(ct);

        _app = await Winche.Console.Identity.ConsoleAccountsApp.StartAsync(ConnectionString, server.MasterKey, server.PreviousMasterKeys, Issuer,
            settings.Current.PublicUrl.TrimEnd('/') + ServerOptions.ConsolePath + "/reset-password", configuration, ct,
            ServerEmail.Of(server)?.For(() => "Winche console"));
        logger.LogInformation("Console accounts are ready (Winche Auth, schema {Schema})", Schema);

        // The console's identity providers are the accounts' Sign-in method providers: kept in step with Server settings.
        _ = Task.Run(async () =>
        {
            try { await MirrorAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Couldn't bring the console accounts' providers in step with Server settings"); }
        });
        settings.Area.Changed += changed => _ = Task.Run(async () =>
        {
            try { await MirrorAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Couldn't bring the console accounts' providers in step with Server settings"); }
        });
    }

    /// <summary>Brings the accounts' Sign-in method providers (and Keycloak sync secrets) in step with Server settings.</summary>
    internal Task MirrorAsync(CancellationToken ct) =>
        new Winche.Console.Identity.ConsoleAccounts(new Winche.Console.Options.ConsoleOptions { Accounts = this }).MirrorProvidersAsync(
            settings.Current.ConsoleProviders, (id, c) => settings.Secrets.GetAsync(Winche.Console.Identity.ConsoleOidcSettings.KeycloakSyncSecretFor(id), c), ct: ct);

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is not null) await _app.StopAsync(ct);
    }
}
