using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;

namespace Winche.Console.Api;

/// <summary>
/// <c>winche login</c> for consoles with their own accounts (a browser approval, then a PKCE code exchange), CI tokens
/// for admins, and revoking either.
/// </summary>
public static class ConsoleTokenEndpoints
{
    /// <param name="UserCode">Instead of the rest: allows the CLI that showed this code (<c>winche login --no-browser</c>).</param>
    public sealed record ApproveRequest(string? Challenge, int Port, string? State, string? Name, string? UserCode = null);
    public sealed record DeviceRequest(string? Challenge, string? Name);
    public sealed record PollRequest(string? DeviceCode);
    public sealed record ExchangeRequest(string? Code, string? Verifier);
    public sealed record CreateRequest(string? Name, string[]? Scopes, int? Days);

    private static IResult Problem(string message) => ConsoleErrors.Problem("tokens", message, null);

    private static string? Email(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Email) ?? u.FindFirstValue("email");
    private static string UserId(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.NameIdentifier) ?? u.FindFirstValue("sub") ?? "";
    private static bool ByToken(ClaimsPrincipal u) => u.FindFirstValue(ConsoleTokenHandler.TokenKindClaim) is not null;   // a token or the CLI, not a browser
    private static bool ByCiToken(ClaimsPrincipal u) => u.FindFirstValue(ConsoleTokenHandler.TokenKindClaim) == "ci";

    public sealed record RefreshRequest(string? RefreshToken, string? Name = null);

    /// <summary>The account's session for the CLI, named so it reads well among the account's sessions.</summary>
    private static Winche.Auth.AuthRequestInfo CliInfo(HttpContext http, string? name) =>
        new(http.Connection.RemoteIpAddress?.ToString(), string.IsNullOrWhiteSpace(name) ? "winche CLI" : name.Trim()[..Math.Min(name.Trim().Length, 80)]);

    private static IResult Session(Winche.Auth.AuthTokens t) => Results.Json(new
    {
        accessToken = t.AccessToken, refreshToken = t.RefreshToken, expiresAt = t.ExpiresAt,
        user = t.User.Email ?? t.User.Uid,
    });

    private static object Info(ConsoleToken t) => new
    {
        id = t.Id, kind = t.Kind, name = t.Name, user = t.UserEmail ?? t.UserId, role = t.Role, scopes = t.Scopes,
        createdAt = t.CreatedAt, expiresAt = t.ExpiresAt, lastUsedAt = t.LastUsedAt,
    };

    public static IEndpointRouteBuilder MapConsoleTokenEndpoints(this IEndpointRouteBuilder app, ConsoleOptions options)
    {
        // ── winche login ──────────────────────────────────────────────────────────────────────────────────────
        {
            // The signed-in user allows the CLI waiting on 127.0.0.1:{port}; the browser goes back to it with a code.
            app.MapPost("/api/cli-login/approve", async (ApproveRequest body, HttpContext http, ConsoleTokenStore store, CancellationToken ct) =>
            {
                // A person approves: in the browser, or a code from a CLI signed in as them (winche login:approve); never a CI token.
                if (ByCiToken(http.User) || (ByToken(http.User) && body.UserCode is null)) return Results.Forbid();
                if (!store.Available) return Problem("The CLI's sign-in needs AddWincheConfig(…) (where its tokens are kept).");
                if (body.UserCode is { } userCode)
                {
                    var who = http.User.IsInRole(ConsoleRoles.Admin) || http.User.IsInRole(ConsoleRoles.Member) || http.User.IsInRole(ConsoleRoles.Viewer)
                        ? ConsoleRolePolicy.Highest(http.User, options).ToString() : "";
                    return await store.ApproveDeviceAsync(userCode, UserId(http.User), Email(http.User), who, ct)
                        ? Results.Json(new { approved = true })
                        : Problem("That code isn't right, has expired or was used: check the one in your terminal.");
                }
                if (body.Challenge is not { Length: 43 } challenge || body.State is not { Length: > 0 and <= 200 } state || body.Port is < 1024 or > 65535)
                    return Problem("That sign-in link isn't complete: run winche login again.");
                var role = http.User.IsInRole(ConsoleRoles.Admin) || http.User.IsInRole(ConsoleRoles.Member) || http.User.IsInRole(ConsoleRoles.Viewer)
                    ? ConsoleRolePolicy.Highest(http.User, options).ToString() : "";
                var name = string.IsNullOrWhiteSpace(body.Name) ? "winche CLI" : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 80)];
                var code = await store.CreateCodeAsync(challenge, UserId(http.User), Email(http.User), role, name, ct);
                return Results.Json(new { redirect = $"http://127.0.0.1:{body.Port}/callback?code={code}&state={Uri.EscapeDataString(state)}" });
            }).RequireAuthorization(ConsoleRoles.SignedInPolicy);   // one sign-in for the server: roles are checked per project, per request

            // The CLI trades the code (with its PKCE verifier) for a session of the account in the console's accounts,
            // like any sign-in (it's among the account's sessions, and ends with "sign out everywhere"): an access token
            // for its requests, and a refresh token to renew it (POST cli-login/refresh).
            app.MapPost("/api/cli-login/token", async (ExchangeRequest body, HttpContext http, ConsoleTokenStore store, ConsoleAccounts accounts, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(body.Code) || string.IsNullOrEmpty(body.Verifier) || !store.Available)
                    return Results.Json(new { error = new { code = "INVALID_CODE", message = "Send the code and verifier." } }, statusCode: 400);
                if (await store.RedeemCodeAsync(body.Code, body.Verifier, ct) is not { } who)
                    return Results.Json(new { error = new { code = "INVALID_CODE", message = "That sign-in has expired or was used: run winche login again." } }, statusCode: 400);
                try
                {
                    var tokens = await accounts.UseAsync(a => a.SignInAsUserAsync(who.UserId, CliInfo(http, who.Name), ct));
                    return Session(tokens);
                }
                catch (Winche.Auth.AuthException ex) { return Results.Json(new { error = new { code = ex.Code, message = ex.Message } }, statusCode: 401); }
            }).AllowAnonymous();

            // winche login --no-browser: the CLI shows a code to type in a browser anywhere, and polls until it's allowed.
            app.MapPost("/api/cli-login/device", async (DeviceRequest body, ConsoleTokenStore store, CancellationToken ct) =>
            {
                if (!store.Available) return Problem("The CLI's sign-in needs AddWincheConfig(…) (where its tokens are kept).");
                if (body.Challenge is not { Length: 43 } challenge) return Problem("Send the PKCE challenge.");
                var name = string.IsNullOrWhiteSpace(body.Name) ? "winche CLI" : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 80)];
                var (deviceCode, userCode) = await store.CreateDeviceAsync(challenge, name, ct);
                return Results.Json(new { deviceCode, userCode, expiresIn = (int)ConsoleTokenStore.DeviceLifetime.TotalSeconds, interval = 2 });
            }).AllowAnonymous();
            app.MapPost("/api/cli-login/device/poll", async (PollRequest body, ConsoleTokenStore store, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(body.DeviceCode)) return Problem("Send the device code.");
                var (status, code) = await store.PollDeviceAsync(body.DeviceCode, ct);
                return Results.Json(new { status, code });
            }).AllowAnonymous();
            // The browser page shows which CLI it's allowing.
            app.MapGet("/api/cli-login/device/{userCode}", async (string userCode, ConsoleTokenStore store, CancellationToken ct) =>
                await store.DeviceNameAsync(userCode, ct) is { } name ? Results.Json(new { name, userCode = ConsoleTokenStore.NormalizeUserCode(userCode) }) : Results.NotFound()
            ).RequireAuthorization(ConsoleRoles.SignedInPolicy);

            // Renews the CLI's sign-in: refused once the session has ended (signed out, everywhere too; account off).
            app.MapPost("/api/cli-login/refresh", async (RefreshRequest body, HttpContext http, ConsoleAccounts accounts, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(body.RefreshToken)) return Results.Json(new { error = new { code = "INVALID_REFRESH_TOKEN", message = "Send the refresh token." } }, statusCode: 400);
                try { return Session(await accounts.UseAsync(a => a.RefreshAsync(body.RefreshToken, CliInfo(http, body.Name), ct))); }
                catch (Winche.Auth.AuthException ex) { return Results.Json(new { error = new { code = ex.Code, message = "Your sign-in has ended: run winche login." } }, statusCode: 401); }
            }).AllowAnonymous();

            // winche logout: ends the session.
            app.MapPost("/api/cli-login/logout", async (RefreshRequest body, ConsoleAccounts accounts, CancellationToken ct) =>
            {
                if (!string.IsNullOrEmpty(body.RefreshToken))
                    try { await accounts.UseAsync(a => a.SignOutAsync(body.RefreshToken, ct)); }
                    catch (Winche.Auth.AuthException) { /* already over */ }
                return Results.NoContent();
            }).AllowAnonymous();
        }

        // ── the caller's own token (winche logout) ──────────────────────────────────────────────────────────────
        app.MapDelete("/api/tokens/current", async (HttpContext http, ConsoleTokenStore store, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue(ConsoleTokenHandler.TokenIdClaim) is not { } id) return Problem("This request wasn't made with a token.");
            await store.RevokeAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy).WithMetadata(new ConsoleCiScope(ConsoleCiScope.Functions), new ConsoleCiScope(ConsoleCiScope.Rules));

        // ── admins: CI tokens (a project's: the server's console has none) ──────────────────────────────────────────
        if (options.ServerConsole) return app;
        var admin = app.MapGroup("/api/tokens").RequireAuthorization(ConsoleRoles.AdminPolicy);

        admin.MapGet("", async (ConsoleTokenStore store, CancellationToken ct) => Results.Json(new
        {
            available = store.Available,
            scopes = ConsoleCiScope.All,
            tokens = (await store.ListAsync(ct)).Select(Info),
        }));

        // A person makes them: in the browser, or signed in with the CLI (winche tokens:create); a CI token can't.
        admin.MapPost("", async (CreateRequest body, HttpContext http, ConsoleTokenStore store, CancellationToken ct) =>
        {
            if (ByCiToken(http.User)) return Results.Forbid();
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (!store.Available) return Problem("CI tokens need AddWincheConfig(…) (where they're kept).");
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 80) return Problem("Name the token (where it's used, e.g. gitlab-main).");
            var scopes = (body.Scopes ?? []).Distinct().ToArray();
            if (scopes.Length == 0 || scopes.Any(s => !ConsoleCiScope.All.Contains(s))) return Problem($"Choose what it may deploy: {string.Join(", ", ConsoleCiScope.All)}.");
            if (body.Days is not (>= 1 and <= 730)) return Problem("A CI token lasts 1 day to 2 years.");
            var (token, secret) = await store.CreateAsync("ci", body.Name.Trim(), UserId(http.User), Email(http.User) ?? http.User.Identity?.Name,
                ConsoleRoles.Admin, scopes, TimeSpan.FromDays(body.Days.Value), ct);
            return Results.Json(new { token = Info(token), secret });
        });

        admin.MapDelete("/{id}", async (string id, HttpContext http, ConsoleTokenStore store, CancellationToken ct) =>
        {
            if (ByCiToken(http.User)) return Results.Forbid();
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            return await store.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
