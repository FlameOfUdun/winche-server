using Npgsql;
using Winche.Core;
using Winche.Secrets;

namespace Winche.Server;

/// <summary>
/// The server's own settings (the console's Server settings, for server admins), kept in the control database's
/// <c>server</c> schema. What the deployment sets (<c>Winche__…</c>) is where they start; a change in the console wins.
/// Only the control database's connection string and the master key are deploy-only.
/// </summary>
public sealed record ServerSettings
{
    public const string Area = "server";

    /// <summary>The address apps reach the server at (tokens' issuer, file links, the console's links).</summary>
    public string PublicUrl { get; init; } = "";


    /// <summary>Console admins need two-step sign-in to change sensitive settings.</summary>
    public bool RequireTwoFactorForSettings { get; init; } = true;

    /// <summary>
    /// The longest any project's function may run, in minutes (default 9, at most 60). Projects set their default and
    /// each function's own time up to it. Changes apply from the next run.
    /// </summary>
    public int FunctionsMaxMinutes { get; init; } = 9;

    /// <summary>The most memory a project's function may ask for, in MB (default 1024). Changes apply from the next run.</summary>
    public int FunctionsMaxMemoryMb { get; init; } = 1024;

    /// <summary>How many functions of a project may run at once on this server (default 16); more wait their turn.</summary>
    public int FunctionsMaxConcurrency { get; init; } = 16;

    /// <summary>
    /// Each worker's heap limit in a project's shared functions process, in MB (default 1024): a run past it stops that
    /// process. A function asking for more runs in a process of its own. Applies when the process next starts.
    /// </summary>
    public int FunctionsSharedProcessMb { get; init; } = 1024;

    /// <summary>The most functions processes per project (default 4: the shared one and isolated functions' own).</summary>
    public int FunctionsMaxProcesses { get; init; } = 4;

    /// <summary>
    /// The identity providers people can sign in to the console with, besides email and password (always there). Each
    /// one's client secret is the secret <see cref="Winche.Console.Identity.ConsoleOidcSettings.SecretFor"/> its id.
    /// </summary>
    public IReadOnlyList<Winche.Console.Identity.ConsoleOidcSettings> ConsoleProviders { get; init; } = [];

    /// <summary>With a provider on: passwords only for server admins (the way in when it's down); everyone else uses one.</summary>
    public bool ConsolePasswordsOnlyForAdmins { get; init; }

    /// <summary>The one provider of before (moved to <see cref="ConsoleProviders"/> at startup); null once moved.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consoleSignIn")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? LegacyConsoleSignIn { get; init; }

    /// <summary>The password of the new-projects database server of before (each project now says where its data goes).</summary>
    public const string LegacyNewProjectsPasswordSecret = "server.newProjects.password";

    /// <summary>The single provider's secret of before, moved to the provider's own.</summary>
    public const string LegacyConsoleSignInSecret = "server.consoleSignIn.clientSecret";

    /// <summary>The id the deployment's provider (Winche__ConsoleSignIn__…), and the one moved from before, get.</summary>
    public const string FirstProviderId = "default";
    /// <summary>The first-run setup code the server made (kept encrypted, so every instance prints the same one).</summary>
    public const string SetupCodeSecret = "server.setupCode";

    /// <summary>The deployment's values: where the settings start.</summary>
    public static ServerSettings From(ServerOptions o) => new()
    {
        PublicUrl = o.PublicUrl.TrimEnd('/'),
        RequireTwoFactorForSettings = o.RequireTwoFactorForSettings,
        FunctionsMaxMinutes = o.FunctionsMaxMinutes,
        FunctionsMaxMemoryMb = o.FunctionsMaxMemoryMb,
        FunctionsMaxConcurrency = o.FunctionsMaxConcurrency,
        FunctionsSharedProcessMb = o.FunctionsSharedProcessMb,
        FunctionsMaxProcesses = o.FunctionsMaxProcesses,
        ConsoleProviders = o.ConsoleSignIn is { Issuer.Length: > 0 } c
            ?
            [
                new Winche.Console.Identity.ConsoleOidcSettings
                {
                    Id = FirstProviderId, Enabled = true, Issuer = c.Issuer, ClientId = c.ClientId, Scopes = c.Scopes ?? "openid profile email",
                    ButtonLabel = c.ButtonLabel, RoleClaim = c.RoleClaim, AdminValues = c.AdminValues,
                    CreateAccounts = c.CreateAccounts, TrustProviderTwoStep = c.TrustProviderTwoStep,
                },
            ]
            : [],
        ConsolePasswordsOnlyForAdmins = o.ConsoleSignIn is { Issuer.Length: > 0, PasswordsOnlyForAdmins: true },
    };

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(u.Query))
            problems.Add("The public address is an http(s) address, like https://api.example.com.");
        if (FunctionsMaxMinutes is < 1 or > 60) problems.Add("Functions may run 1 to 60 minutes at most.");
        var budget = Winche.Functions.FunctionsMemoryBudget.FromContainer().TotalMb;
        if (FunctionsMaxMemoryMb < 64 || FunctionsMaxMemoryMb > budget)
            problems.Add($"A function's memory is 64 MB to {budget} MB at most (this server's functions memory).");
        if (FunctionsSharedProcessMb < 128 || FunctionsSharedProcessMb > budget)
            problems.Add($"The shared process's limit is 128 MB to {budget} MB (this server's functions memory).");
        if (FunctionsMaxConcurrency is < 1 or > 256) problems.Add("Functions running at once is 1 to 256 per project.");
        if (FunctionsMaxProcesses is < 1 or > 16) problems.Add("Functions processes per project are 1 to 16.");
        problems.AddRange(Winche.Console.Identity.ConsoleOidcSettings.Problems(ConsoleProviders));
        return problems;
    }
}

/// <summary>The server settings in force, for the router, the project hosts and the console's Server settings.</summary>
public sealed class ServerSettingsProvider(SettingsArea<ServerSettings> area, SettingsStore store, SecretStore secrets, ServerOptions deployment)
{
    public int PreviousMasterKeyCount => deployment.PreviousMasterKeys.Length;

    /// <summary>The deployment's client secret for its console sign-in provider, until one is saved in Server settings.</summary>
    public string? DeploymentConsoleSignInSecret => deployment.ConsoleSignIn?.ClientSecret;

    /// <summary>A console sign-in provider's client secret: saved in Server settings, else (the deployment's provider) the deployment's.</summary>
    public async Task<string?> ConsoleProviderSecretAsync(string id, CancellationToken ct) =>
        await secrets.GetAsync(Winche.Console.Identity.ConsoleOidcSettings.SecretFor(id), ct)
        ?? (id == ServerSettings.FirstProviderId ? DeploymentConsoleSignInSecret : null);

    /// <summary>
    /// Moves the one console sign-in provider of before into the list (as its first, with its secret), and the
    /// passwords-only-for-admins switch onto the server. Safe to repeat: nothing to do once moved.
    /// </summary>
    public async Task MoveLegacyConsoleSignInAsync(CancellationToken ct)
    {
        var now = area.Current;
        if (now.LegacyConsoleSignIn is not { } old) return;
        var moved = now with { LegacyConsoleSignIn = null };
        var issuer = (string?)old["issuer"];
        if (!string.IsNullOrWhiteSpace(issuer) && now.ConsoleProviders.Count == 0)
        {
            var provider = System.Text.Json.JsonSerializer.Deserialize<Winche.Console.Identity.ConsoleOidcSettings>(old, SettingsArea.Json)! with { Id = ServerSettings.FirstProviderId };
            moved = moved with
            {
                ConsoleProviders = [provider],
                ConsolePasswordsOnlyForAdmins = old["passwordsOnlyForAdmins"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var only) && only,
            };
            if (await secrets.GetAsync(ServerSettings.LegacyConsoleSignInSecret, ct) is { Length: > 0 } secret)
                await secrets.SetAsync(Winche.Console.Identity.ConsoleOidcSettings.SecretFor(ServerSettings.FirstProviderId), secret, "winche", ct);
        }
        await store.SaveAsync(area.Name, System.Text.Json.JsonSerializer.SerializeToNode(moved, SettingsArea.Json)!, "winche",
            "Console sign-in: the identity provider moved into the providers list", ct);
        await secrets.DeleteAsync(ServerSettings.LegacyConsoleSignInSecret, ct);
    }

    /// <summary>The first-run setup code: the deployment's, else one made once and kept (encrypted) with the secrets.</summary>
    public async Task<string> SetupCodeAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(deployment.SetupCode)) return deployment.SetupCode.Trim();
        if (await secrets.GetAsync(ServerSettings.SetupCodeSecret, ct) is { Length: > 0 } saved) return saved;
        // Three groups of four, from letters and digits that can't be mistaken for each other.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(alphabet, 12);
        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
        await secrets.SetAsync(ServerSettings.SetupCodeSecret, code, "winche", ct);
        return await secrets.GetAsync(ServerSettings.SetupCodeSecret, ct) ?? code;   // another instance may have saved first
    }
    /// <summary>The settings in force; the public address is always the deployment's (it's where the server is reached).</summary>
    public ServerSettings Current => area.Current with { PublicUrl = Deployment.PublicUrl };
    public ServerSettings Deployment { get; } = ServerSettings.From(deployment);
    public SettingsArea<ServerSettings> Area => area;
    public SettingsStore Store => store;
    public SecretStore Secrets => secrets;

}

/// <summary>The server's settings as each project's console shows them to server admins.</summary>
internal sealed class ServerSettingsForConsole(ServerSettingsProvider settings, ProjectHosts projects) : Winche.Console.Project.IWincheServerSettings
{
    /// <summary>A Keycloak sync's client secret goes to the console accounts at once.</summary>
    public Task SecretChangedAsync(string name, CancellationToken ct) =>
        name.EndsWith(".keycloakSync.clientSecret", StringComparison.Ordinal) ? projects.Accounts.MirrorAsync(ct) : Task.CompletedTask;

    public SettingsArea Area => settings.Area;
    public SettingsStore Store => settings.Store;
    public SecretStore Secrets => settings.Secrets;
    public System.Text.Json.Nodes.JsonNode DeploymentJson => System.Text.Json.JsonSerializer.SerializeToNode(settings.Deployment, SettingsArea.Json)!;
    /// <summary>The server's passwords, and each console sign-in provider's client secret.</summary>
    public IReadOnlyDictionary<string, string> SecretNames => new Dictionary<string, string>
    {
    }.Concat(settings.Current.ConsoleProviders.Select(p => KeyValuePair.Create(
        Winche.Console.Identity.ConsoleOidcSettings.SecretFor(p.Id), $"The console's client secret at {p.Label.Replace("Sign in with ", "")}")))
        .Concat(settings.Current.ConsoleProviders.Where(p => p.KeycloakSync is not null).Select(p => KeyValuePair.Create(
            Winche.Console.Identity.ConsoleOidcSettings.KeycloakSyncSecretFor(p.Id), $"The Keycloak sync client's secret for {p.Label.Replace("Sign in with ", "")}")))
        .ToDictionary();

    public IReadOnlyList<Winche.Console.Identity.ConsoleOidcSettings> ConsoleProviders => settings.Current.ConsoleProviders;

    public bool ConsolePasswordsOnlyForAdmins => settings.Current.ConsolePasswordsOnlyForAdmins && settings.Current.ConsoleProviders.Any(p => p.Enabled);

    public Task<string?> ConsoleProviderSecretAsync(string id, CancellationToken ct) => settings.ConsoleProviderSecretAsync(id, ct);

    public string ConsoleSignInCallbackUrl => settings.Current.PublicUrl.TrimEnd('/') + "/_console/api/auth/oidc/callback";

    public string ConsoleSignInReturnPath => "/_console/";

}

/// <summary>Makes the control database's schemas the server's own settings need, before anything reads them.</summary>
/// <summary>Moves the console sign-in settings of before into today's shape, once the server's settings are loaded.</summary>
internal sealed class ConsoleSignInMove(ServerSettingsProvider settings) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await settings.MoveLegacyConsoleSignInAsync(ct);
        await settings.Secrets.DeleteAsync(ServerSettings.LegacyNewProjectsPasswordSecret, ct);   // nothing reads it now
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class ControlSchemaSetup(ServerOptions server) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = NpgsqlDataSource.Create(server.ConnectionString);
        await using var cmd = db.CreateCommand("CREATE SCHEMA IF NOT EXISTS server");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

