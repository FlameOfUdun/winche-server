using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Identity;
using Winche.Console.Options;

namespace Winche.Console.Api;

/// <summary>
/// A project's service accounts: the token exchange (<c>service-accounts/token</c>: a key or a trusted identity's token
/// for an hour's access token) and, for admins, the accounts, their keys and trusted identities.
/// </summary>
public static class ServiceAccountEndpoints
{
    public sealed record TokenRequest(string? Key, string? Assertion);
    public sealed record AccountRequest(string? Name, string? Role, string[]? Scopes, bool? Disabled);
    public sealed record KeyRequest(int? Days);
    public sealed record IdentityRequest(string? Issuer, string? Subject, string? Audience);

    private static readonly string[] Roles = [ConsoleRoles.Viewer, ConsoleRoles.Member, ConsoleRoles.Admin];

    private static IResult Problem(string message) => ConsoleErrors.Problem("serviceAccounts", message, null);
    private static IResult Refused(string code, string message, int status) => Results.Json(new { error = new { code, message } }, statusCode: status);

    /// <summary>This project's API address: what an access token is for.</summary>
    internal static string ApiOf(HttpContext http) => $"{http.Request.PathBase}{http.RequestServices.GetRequiredService<ConsolePrefix>().Api}";

    private static string? Email(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Email) ?? u.FindFirstValue("email");
    private static string UserId(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.NameIdentifier) ?? u.FindFirstValue("sub") ?? "";
    private static bool ByToken(ClaimsPrincipal u) => u.FindFirstValue(ConsoleTokenHandler.TokenKindClaim) is "ci" or "service";

    public static IEndpointRouteBuilder MapConsoleServiceAccountEndpoints(this IEndpointRouteBuilder app, ConsoleOptions options)
    {
        // ── the exchange: a key or a trusted identity's token → an access token for an hour ───────────────────────
        app.MapPost("/service-accounts/token", async (TokenRequest body, HttpContext http, ServiceAccountStore store, ServiceAccountTokens tokens,
            TrustedIdentityValidator validator, CancellationToken ct) =>
        {
            if (!store.Available) return Problem("Service accounts can't be kept on this server.");
            ServiceAccount account;
            string credential, how;
            if (body.Key is { Length: > 0 } key)
            {
                if (!options.ServiceAccountKeysAllowed)
                    return Refused("KEYS_NOT_ALLOWED", "This server doesn't take service account keys: use a trusted identity.", 403);
                if (await store.FindByKeyAsync(key.Trim(), http.Connection.RemoteIpAddress?.ToString(), ct) is not { } found)
                    return Refused("INVALID_KEY", "That key isn't valid (wrong, expired or revoked), or its account is turned off.", 401);
                (account, credential, how) = (found.Account, found.Key.Id, "key");
            }
            else if (body.Assertion is { Length: > 0 } assertion)
            {
                if (TrustedIdentityValidator.IssuerOf(assertion) is not { } issuer)
                    return Refused("INVALID_ASSERTION", "That isn't a signed token (a JWT).", 401);
                string? problem = null;
                (ServiceAccount, ServiceAccountIdentity)? match = null;
                foreach (var identity in (await store.IdentitiesAsync(null, ct)).Where(i => i.Issuer == issuer))
                {
                    var (claims, why) = await validator.ValidateAsync(assertion, issuer, identity.Audience ?? "", ct);
                    if (claims is null) { problem ??= why; continue; }
                    var subject = claims.TryGetValue("sub", out var sub) ? sub?.ToString() ?? "" : "";
                    if (!ServiceAccountStore.Matches(identity.Subject, subject)) { problem ??= $"No service account trusts the subject {subject}."; continue; }
                    if (await store.GetAsync(identity.AccountId, ct) is { Disabled: false } trusting) { match = (trusting, identity); break; }
                }
                if (match is not var (matched, used))
                    return Refused("UNTRUSTED", problem ?? $"No service account here trusts {issuer}.", 401);
                await store.TouchIdentityAsync(used, ct);
                (account, credential, how) = (matched, used.Id, "identity");
            }
            else return Problem("Send a key, or a token from a trusted identity (assertion).");
            var (accessToken, expiresAt) = tokens.Issue(account.Id, credential, ApiOf(http));
            return Results.Json(new
            {
                accessToken, expiresAt, tokenType = "Bearer",
                account = new { id = account.Id, name = account.Name, role = account.Role, scopes = account.Scopes, via = how },
            });
        }).AllowAnonymous();

        // ── admins: the accounts, their keys and trusted identities ───────────────────────────────────────────────
        var admin = app.MapGroup("/service-accounts").RequireAuthorization(ConsoleRoles.AdminPolicy);

        admin.MapGet("", async (HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            var list = new List<object>();
            var accounts = http.RequestServices.GetService<ConsoleAccounts>();
            var stale = TimeSpan.FromDays(90);
            foreach (var a in await store.ListAsync(ct))
            {
                var keys = await store.KeysAsync(a.Id, ct);
                list.Add(new
                {
                    id = a.Id, name = a.Name, role = a.Role, scopes = a.Scopes, disabled = a.Disabled,
                    createdBy = a.CreatedByEmail ?? a.CreatedBy, createdAt = a.CreatedAt, lastUsedAt = a.LastUsedAt,
                    // The removed-admin review: an account whose maker is no longer an admin here is flagged for a look.
                    makerIsAdmin = accounts is null || await MakerIsAdminAsync(a.CreatedBy, accounts, options, http.RequestServices, ct),
                    keys = keys.Select(k => new
                    {
                        id = k.Id, createdBy = k.CreatedBy, createdAt = k.CreatedAt, expiresAt = k.ExpiresAt, lastUsedAt = k.LastUsedAt,
                        lastUsedFrom = k.LastUsedFrom,
                        unused = DateTimeOffset.UtcNow - (k.LastUsedAt ?? k.CreatedAt) > stale,   // flagged after 90 days without use
                    }),
                    identities = (await store.IdentitiesAsync(a.Id, ct)).Select(i => new
                    {
                        id = i.Id, issuer = i.Issuer, subject = i.Subject, audience = i.Audience, createdAt = i.CreatedAt, lastUsedAt = i.LastUsedAt,
                    }),
                });
            }
            return Results.Json(new
            {
                available = store.Available, roles = Roles, scopes = ConsoleCiScope.All, maxKeys = ServiceAccountStore.MaxKeys,
                maxKeyDays = options.ServiceAccountKeyMaxDays, keysAllowed = options.ServiceAccountKeysAllowed, accounts = list,
            });
        });

        admin.MapPost("", async (AccountRequest body, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 80 || !body.Name.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                return Problem("Name it after what uses it (letters, digits, - _ .), e.g. billing-server or ci-deploy.");
            if (Check(body) is { } wrong) return Problem(wrong);
            var account = await store.CreateAsync(body.Name.Trim(), RoleOf(body.Role), body.Scopes ?? [], UserId(http.User), Email(http.User) ?? http.User.Identity?.Name, ct);
            return account is null ? Problem($"There's already a service account named {body.Name.Trim()}.") : Results.Json(new { id = account.Id, name = account.Name });
        });

        admin.MapPut("/{id}", async (string id, AccountRequest body, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            if (Check(body) is { } wrong) return Problem(wrong);
            return await store.UpdateAsync(id, RoleOf(body.Role), body.Scopes ?? [], body.Disabled ?? false, ct) ? Results.NoContent() : Results.NotFound();
        });

        admin.MapDelete("/{id}", async (string id, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            return await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });

        admin.MapPost("/{id}/keys", async (string id, KeyRequest body, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            if (!options.ServiceAccountKeysAllowed) return Problem("This server doesn't allow service account keys: give the account a trusted identity.");
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            if (body.Days is not { } days || days < 1 || days > options.ServiceAccountKeyMaxDays)
                return Problem($"A key lasts 1 to {options.ServiceAccountKeyMaxDays} days.");
            if (await store.AddKeyAsync(id, TimeSpan.FromDays(days), Email(http.User) ?? UserId(http.User), ct) is not var (key, secret))
                return Problem($"An account has at most {ServiceAccountStore.MaxKeys} keys: revoke the old one once nothing uses it.");
            return Results.Json(new { key = new { id = key.Id, createdAt = key.CreatedAt, expiresAt = key.ExpiresAt }, secret });
        });

        admin.MapDelete("/{id}/keys/{keyId}", async (string id, string keyId, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            return await store.RevokeKeyAsync(id, keyId, ct) ? Results.NoContent() : Results.NotFound();
        });

        admin.MapPost("/{id}/identities", async (string id, IdentityRequest body, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            if (!Uri.TryCreate(body.Issuer?.Trim(), UriKind.Absolute, out var issuer)
                || !(issuer.Scheme == "https" || (issuer.Scheme == "http" && issuer.IsLoopback)))
                return Problem("The issuer is its https address, as its tokens say (iss), e.g. https://gitlab.example.com.");
            if (string.IsNullOrWhiteSpace(body.Subject) || body.Subject.Trim() == "*")
                return Problem("Say which of its subjects to trust (sub), e.g. project_path:team/app:ref_type:branch:ref:main; * matches any run of characters.");
            if (string.IsNullOrWhiteSpace(body.Audience))
                return Problem("Say the audience its tokens are made for (aud): the one the CI job asks for, e.g. this project's address.");
            var identity = await store.AddIdentityAsync(id, body.Issuer!.Trim().TrimEnd('/'), body.Subject.Trim(), body.Audience.Trim(), Email(http.User) ?? UserId(http.User), ct);
            return Results.Json(new { id = identity.Id });
        });

        admin.MapDelete("/{id}/identities/{identityId}", async (string id, string identityId, HttpContext http, ServiceAccountStore store, CancellationToken ct) =>
        {
            if (await Guard(http) is { } refused) return refused;
            return await store.RemoveIdentityAsync(id, identityId, ct) ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    /// <summary>People manage service accounts (never a token), with two-step sign-in where the console requires it.</summary>
    private static async Task<IResult?> Guard(HttpContext http)
    {
        if (ByToken(http.User)) return Results.Forbid();
        return await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http);
    }

    private static string? Check(AccountRequest body)
    {
        var scopes = body.Scopes ?? [];
        if (body.Role is { } role && RoleOf(role) is null) return "The role is viewer, member or admin.";
        if (scopes.Any(s => !ConsoleCiScope.All.Contains(s))) return $"Deploy scopes are {string.Join(", ", ConsoleCiScope.All)}.";
        if ((body.Role is null) == (scopes.Length == 0)) return "Give it either a role (what it may do with the project's data and settings) or deploy scopes (what it may deploy), not both.";
        return null;
    }

    /// <summary>The role as the console names it (Viewer, Member, Admin), whatever its case; null for none or an unknown one.</summary>
    private static string? RoleOf(string? role) => role is null ? null : Roles.FirstOrDefault(r => string.Equals(r, role.Trim(), StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> MakerIsAdminAsync(string userId, ConsoleAccounts accounts, ConsoleOptions options, IServiceProvider services, CancellationToken ct)
    {
        if (await accounts.FindAsync(userId, ct) is not { Active: true } user) return false;
        return await AuthEndpoints.RoleHereAsync(user, options, services) == ConsoleRoles.Admin;
    }
}
