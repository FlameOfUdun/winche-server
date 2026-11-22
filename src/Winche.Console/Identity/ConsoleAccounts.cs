using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth;
using Winche.Auth.DependencyInjection;
using Winche.Console.Options;
using Winche.Core;
using Winche.Secrets;

namespace Winche.Console.Identity;

/// <summary>
/// Where the console's accounts are: a Winche Auth of their own (for the people who run the server, not any app's users),
/// running as its own app inside the host. The host starts it (<see cref="ConsoleAccountsApp.StartAsync"/>) and gives
/// every console the same one (<see cref="ConsoleOptions.Accounts"/>).
/// </summary>
public interface IConsoleAccountsHost
{
    /// <summary>Its services: <see cref="IWincheAuth"/> (in a scope), its settings and secrets.</summary>
    IServiceProvider Services { get; }
}

/// <summary>Starts the console accounts' Winche Auth: its own app, with no address of its own.</summary>
public static class ConsoleAccountsApp
{
    /// <param name="connectionString">Its tables, settings and secrets (a schema of their own).</param>
    /// <param name="issuer">The issuer of its tokens (they never leave the server).</param>
    /// <param name="actionUrl">The console page its emailed links open (password resets).</param>
    public static async Task<WebApplication> StartAsync(string connectionString, string masterKey, string[] previousMasterKeys, string issuer,
        string actionUrl, IConfiguration? configuration = null, CancellationToken ct = default, Winche.Auth.Email.IAuthEmailSender? email = null,
        Action<IServiceCollection>? secrets = null)
    {
        var builder = WebApplication.CreateBuilder();
        if (configuration is not null) builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddSingleton<IServer, NoServer>();   // no sockets: nothing reaches it from outside
        builder.Services.AddWincheConfig(o =>
        {
            o.ConnectionString = connectionString;
            o.MasterKey = masterKey;
            o.PreviousMasterKeys = previousMasterKeys;
        });
        Winche.Secrets.SecretsServiceCollectionExtensions.AddWincheSecrets(builder.Services);
        secrets?.Invoke(builder.Services);   // the host's sources (AWS) and where it keeps its own secrets there
        builder.Services.AddWincheAuth(o =>
        {
            o.ConnectionString = connectionString;
            o.Issuer = issuer;
            o.EmailPassword(p => p.MinLength = 8);   // as console accounts always had (changeable in its settings)
            o.Mfa(m => m.Issuer = "Winche console");
            // With the host's own email, its emails go out with nothing to set up; otherwise links are only logged until
            // an SMTP server is chosen.
            o.Email(e => { e.AppName = "Winche console"; e.ActionUrl = actionUrl; e.SendVerificationOnSignUp = false; if (email is null) e.LogLinks(); });
            o.UseSettings();
        });
        if (email is not null) builder.Services.AddSingleton(email);
        var app = builder.Build();
        await app.StartAsync(ct);
        return app;
    }

    /// <summary>A server that takes no requests: the accounts are used through their services only.</summary>
    private sealed class NoServer : IServer
    {
        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}

/// <summary>A console account, as the console shows and checks it.</summary>
public sealed record ConsoleAccount(
    string Id, string? Email, string? FirstName, string? LastName, string? Role, bool Active, bool TwoFactorEnabled, bool TwoFactorRequired,
    bool LockedOut, bool HasPassword, IReadOnlyList<string> Providers, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt)
{
    public bool IsAdmin => Role == ConsoleRoles.Admin;
    public string Name => string.Join(' ', new[] { FirstName, LastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } n ? n : Email ?? Id;
}

/// <summary>
/// The console's accounts over its Winche Auth: what the console keeps on an account (its role, first and last name, two-step
/// required by an admin, the identity providers it signs in with) lives in the account's custom claims.
/// </summary>
public sealed class ConsoleAccounts(ConsoleOptions options)
{
    internal const string RoleClaim = "role", FirstNameClaim = "firstName", LastNameClaim = "lastName", TwoStepRequiredClaim = "twoStepRequired";

    private IServiceProvider Root => options.Accounts?.Services ?? throw new InvalidOperationException("The console has no accounts (ConsoleOptions.Accounts).");

    /// <summary>Runs <paramref name="work"/> with the accounts' Winche Auth, in a scope of its own.</summary>
    public async Task<T> UseAsync<T>(Func<IWincheAuth, Task<T>> work)
    {
        await using var scope = Root.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<IWincheAuth>());
    }

    public Task UseAsync(Func<IWincheAuth, Task> work) => UseAsync(async a => { await work(a); return true; });

    internal static ConsoleAccount From(WincheUserInfo u) => new(
        u.Uid, u.Email, Text(u.CustomClaims, FirstNameClaim), Text(u.CustomClaims, LastNameClaim), Text(u.CustomClaims, RoleClaim) is { } r && ConsoleRoles.All.Contains(r) ? r : null,
        !u.Disabled, u.MfaEnabled, Flag(u.CustomClaims, TwoStepRequiredClaim), u.LockedOutUntil is not null, u.Providers.Contains("password"),
        u.Providers, u.CreatedAt, u.LastSignInAt);

    private static string? Text(IReadOnlyDictionary<string, object?> c, string key) => c.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;
    private static bool Flag(IReadOnlyDictionary<string, object?> c, string key) => c.TryGetValue(key, out var v) && v is true or "true";

    public Task<ConsoleAccount?> FindAsync(string? id, CancellationToken ct = default) =>
        string.IsNullOrEmpty(id) ? Task.FromResult<ConsoleAccount?>(null) : UseAsync(async a => await a.GetUserAsync(id, ct) is { } u ? From(u) : null);

    public Task<ConsoleAccount?> FindByEmailAsync(string? email, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(email) ? Task.FromResult<ConsoleAccount?>(null) : UseAsync(async a => await a.FindByEmailAsync(email.Trim(), ct) is { } u ? From(u) : null);

    /// <summary>The signed-in person's account (the cookie's or a CI token's user).</summary>
    public Task<ConsoleAccount?> CurrentAsync(ClaimsPrincipal user, CancellationToken ct = default) => FindAsync(user.FindFirstValue(ClaimTypes.NameIdentifier), ct);

    /// <summary>Every account (the console has few).</summary>
    public Task<IReadOnlyList<ConsoleAccount>> ListAsync(CancellationToken ct = default) => UseAsync<IReadOnlyList<ConsoleAccount>>(async a =>
    {
        var all = new List<ConsoleAccount>();
        string? page = null;
        do
        {
            var p = await a.ListUsersPageAsync(page, 500, ct);
            all.AddRange(p.Users.Select(From));
            page = p.NextPageToken;
        }
        while (page is not null);
        return all;
    });

    public Task<bool> AnyAsync(CancellationToken ct = default) => UseAsync(async a => (await a.ListUsersAsync(null, 0, 1, ct)).Total > 0);

    /// <summary>A new account: with a password, or none yet (it gets one through a reset link, or signs in with a provider).</summary>
    public Task<ConsoleAccount> CreateAsync(string email, string? password, string? firstName, string? lastName, string? role, bool twoStepRequired = false,
        CancellationToken ct = default) => UseAsync(async a =>
    {
        var name = string.Join(' ', new[] { firstName, lastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
        var u = await a.CreateUserAsync(email.Trim(), string.IsNullOrEmpty(password) ? null : password, name.Length > 0 ? name : null, ct);
        u = await a.SetEmailVerifiedAsync(u.Uid, true, ct);   // console accounts are made by an admin, or from a verified provider address
        var claims = new Dictionary<string, object?>
        {
            [RoleClaim] = role, [FirstNameClaim] = Blank(firstName), [LastNameClaim] = Blank(lastName), [TwoStepRequiredClaim] = twoStepRequired ? true : null,
        };
        return From(await a.SetCustomClaimsAsync(u.Uid, Clean(claims), ct));
    });

    /// <summary>Changes what the console keeps on an account: null leaves a value as it is.</summary>
    public Task<ConsoleAccount> UpdateAsync(string id, string? firstName = null, string? lastName = null, string? role = null, bool? twoStepRequired = null,
        bool clearRole = false, CancellationToken ct = default) => UseAsync(async a =>
    {
        var u = await a.GetUserAsync(id, ct) ?? throw new AuthException(AuthErrorCodes.UserNotFound, "There's no such account.");
        var claims = new Dictionary<string, object?>(u.CustomClaims, StringComparer.Ordinal);
        if (firstName is not null) claims[FirstNameClaim] = Blank(firstName);
        if (lastName is not null) claims[LastNameClaim] = Blank(lastName);
        if (role is not null || clearRole) claims[RoleClaim] = role;
        if (twoStepRequired is { } t) claims[TwoStepRequiredClaim] = t ? true : null;
        u = await a.SetCustomClaimsAsync(id, Clean(claims), ct);
        if (firstName is not null || lastName is not null)
        {
            var c = From(u);
            var name = string.Join(' ', new[] { c.FirstName, c.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
            u = await a.PatchProfileAsync(id, Winche.Auth.ProfilePatch.Name(name.Length > 0 ? name : null), ct);   // the name only: a photo stays
        }
        return From(u);
    });

    /// <summary>
    /// The console's identity providers as the accounts' Sign-in method providers (named by their id): its ID tokens are
    /// checked there, accounts linked there, Keycloak synced there, and its back-channel logouts end sessions there.
    /// <paramref name="only"/>: just that provider (made sure of before a sign-in), else the whole list (others removed).
    /// </summary>
    /// <param name="syncReference">Where a provider's sync secret is read from, when the server keeps it as a reference
    /// (in AWS): the accounts keep the same reference, never a copy of the value.</param>
    public async Task MirrorProvidersAsync(IReadOnlyList<ConsoleOidcSettings> providers, Func<string, CancellationToken, Task<string?>> syncSecret,
        string? only = null, CancellationToken ct = default, Func<string, CancellationToken, Task<Winche.Secrets.SecretReference?>>? syncReference = null)
    {
        await using var scope = Root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var area = sp.GetRequiredService<SettingsArea<Winche.Auth.Settings.WincheAuthSettings>>();
        var store = sp.GetRequiredService<SettingsStore>();
        var secrets = sp.GetRequiredService<SecretStore>();
        static Winche.Auth.Settings.SignInProviderSettings As(ConsoleOidcSettings p) => new()
        {
            Name = p.Id, Kind = "oidc", DisplayName = p.Label.Replace("Sign in with ", ""), Authority = p.Issuer?.TrimEnd('/'),
            Audiences = p.ClientId is { Length: > 0 } c ? [c] : [], CountsAsSecondStep = p.TrustProviderTwoStep, KeycloakSync = p.KeycloakSync,
        };
        var wanted = providers.Where(p => p.Enabled && (only is null || p.Id == only)).Select(As).ToList();
        var current = area.Current;
        var next = only is null ? wanted : [.. current.Providers.Where(p => p.Name != only), .. wanted];
        var json = System.Text.Json.JsonSerializer.Serialize<object>(next, SettingsArea.Json);
        if (json != System.Text.Json.JsonSerializer.Serialize<object>(current.Providers, SettingsArea.Json))
            await store.SaveAsync(Winche.Auth.Settings.WincheAuthSettings.Area,
                System.Text.Json.JsonSerializer.SerializeToNode(current with { Providers = next }, SettingsArea.Json)!, "winche",
                "The console's identity providers (Server settings)", ct);
        foreach (var p in providers.Where(p => p.KeycloakSync is not null && (only is null || p.Id == only)))
        {
            var name = Winche.Auth.Settings.KeycloakSyncSettings.SecretName(p.Id);
            if (syncReference is not null && await syncReference(p.Id, ct) is { } reference)
            {
                if ((await secrets.InfoAsync(name, ct))?.Reference?.ToString() != reference.ToString())
                    await secrets.SetReferenceAsync(name, reference, "winche", allowAppCredentials: true, ct);
            }
            else if (await syncSecret(p.Id, ct) is { Length: > 0 } secret && await secrets.GetAsync(name, ct) != secret)
                await secrets.SetAsync(name, secret, "winche", ct);
        }
        // A provider gone, or no longer synced: its sync secret goes from the accounts too (here only: the server's is its own).
        if (only is null)
            foreach (var kept in await secrets.ListAsync("auth.provider.", ct))
                if (kept.Name.Split('.') is ["auth", "provider", var id, "keycloakSync", "clientSecret"]
                    && !providers.Any(p => p.Id == id && p.KeycloakSync is not null))
                    await secrets.DeleteAsync(kept.Name, keepInSource: true, ct);
    }

    /// <summary>How many active server admins there are (the last one can't be demoted, turned off or deleted).</summary>
    public async Task<int> AdminCountAsync(CancellationToken ct = default) => (await ListAsync(ct)).Count(x => x.IsAdmin && x.Active);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static Dictionary<string, object?> Clean(Dictionary<string, object?> claims) =>
        claims.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

/// <summary>
/// Marks the endpoints that act on the console's accounts (Server settings → Authentication): their requests resolve
/// services from the accounts' Winche Auth first, then the console's own.
/// </summary>
public sealed class ConsoleAccountsEndpoint
{
    internal static async Task UseAccountsServicesAsync(Microsoft.AspNetCore.Http.HttpContext http, Func<Task> next)
    {
        if (Microsoft.AspNetCore.Http.EndpointHttpContextExtensions.GetEndpoint(http)?.Metadata.GetMetadata<ConsoleAccountsEndpoint>() is null
            || http.RequestServices.GetService<ConsoleOptions>()?.Accounts is not { } accounts)
        {
            await next();
            return;
        }
        await using var scope = accounts.Services.CreateAsyncScope();
        var own = http.RequestServices;
        http.RequestServices = new Layered(scope.ServiceProvider, own);
        try { await next(); }
        finally { http.RequestServices = own; }
    }

    /// <summary>The accounts' services, then the console's.</summary>
    private sealed class Layered(IServiceProvider first, IServiceProvider then) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type type) =>
            type == typeof(IServiceProvider) || type == typeof(IServiceProviderIsService) ? this : first.GetService(type) ?? then.GetService(type);

        public bool IsService(Type type) =>
            first.GetService<IServiceProviderIsService>()?.IsService(type) == true || then.GetService<IServiceProviderIsService>()?.IsService(type) == true;
    }
}
