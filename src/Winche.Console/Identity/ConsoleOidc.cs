using static Winche.Console.Email.ConsoleLinks;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Winche.Console.Options;

namespace Winche.Console.Identity;

/// <summary>
/// Console sign-in through an OpenID Connect identity provider (Okta, Entra ID, Keycloak, Google Workspace, …), next to
/// console accounts: people sign in at the provider and get a console account, linked to them, the first time.
/// </summary>
public sealed record ConsoleOidcSettings
{
    /// <summary>The provider's id among the console's providers (lowercase letters, digits and dashes; names its secret).</summary>
    public string Id { get; init; } = "";

    public bool Enabled { get; init; }

    /// <summary>The provider's issuer, e.g. https://login.example.com/realms/staff (its discovery document is under it).</summary>
    public string? Issuer { get; init; }

    public string? ClientId { get; init; }

    public string Scopes { get; init; } = "openid profile email";

    /// <summary>What the sign-in button says, e.g. "Sign in with Okta".</summary>
    public string? ButtonLabel { get; init; }

    /// <summary>
    /// The claim that carries people's groups or roles (e.g. <c>groups</c>, or <c>realm_access.roles</c> for Keycloak);
    /// empty: roles are given in the console, not by the provider.
    /// </summary>
    public string? RoleClaim { get; init; }

    /// <summary>Values of <see cref="RoleClaim"/> that make someone a server admin (Admin everywhere).</summary>
    public IReadOnlyList<string> AdminValues { get; init; } = [];

    /// <summary>People the console doesn't know yet get an account at their first sign-in; off: an admin adds them first.</summary>
    public bool CreateAccounts { get; init; } = true;

    /// <summary>Signing in through the provider counts as two-step sign-in (it does its own); off: console two-step too.</summary>
    public bool TrustProviderTwoStep { get; init; } = true;

    /// <summary>
    /// Keeps its people in step with Keycloak (a Keycloak realm's provider): turned off or deleted there, their console
    /// account follows. Its client secret is the server secret <see cref="KeycloakSyncSecretFor"/>.
    /// </summary>
    public Winche.Auth.Settings.KeycloakSyncSettings? KeycloakSync { get; init; }

    /// <summary>The server secret holding a provider's Keycloak sync client secret.</summary>
    public static string KeycloakSyncSecretFor(string providerId) => $"server.consoleSignIn.{providerId}.keycloakSync.clientSecret";

    /// <summary>
    /// Hosts only reachable locally, where plain http is fine: this machine, Docker's host, and one-word names on a
    /// Docker network (http://keycloak:8080).
    /// </summary>
    private static bool LocalOnly(Uri u) => u.IsLoopback || u.Host == "host.docker.internal" || !u.Host.Contains('.');

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Enabled) return problems;
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var u) || (u.Scheme != "https" && !(u.Scheme == "http" && LocalOnly(u))))
            problems.Add("The issuer is the provider's https address, like https://login.example.com/realms/staff (http only for a provider on this machine or its Docker network).");
        if (string.IsNullOrWhiteSpace(ClientId)) problems.Add("Give the client ID the provider gave the console.");
        if (!Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid")) problems.Add("The scopes include openid.");
        return problems;
    }

    /// <summary>A provider's client secret's name with the server's secrets (Server settings → Authentication → Sign-in method).</summary>
    public static string SecretFor(string providerId) => $"server.consoleSignIn.{providerId}.clientSecret";

    /// <summary>What the sign-in button says, when the settings don't.</summary>
    public string Label => string.IsNullOrWhiteSpace(ButtonLabel) ? "Sign in with your organization" : ButtonLabel.Trim();

    /// <summary>The checks for the console's list of providers: each one's own, and ids that name one each.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<ConsoleOidcSettings> providers)
    {
        var problems = new List<string>();
        foreach (var p in providers)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9][a-z0-9-]{0,39}$"))
                problems.Add($"'{p.Id}' isn't a provider id: lowercase letters, digits and dashes, up to 40.");
            problems.AddRange(p.Problems().Select(x => providers.Count > 1 ? $"{p.Label}: {x}" : x));
        }
        foreach (var dup in providers.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            problems.Add($"Two providers have the id '{dup.Key}'.");
        return problems;
    }

    /// <summary>
    /// The account role <paramref name="values"/> map to: Admin (a server admin), or null. What people may do in a
    /// project is that project's members list.
    /// </summary>
    public string? RoleFor(IReadOnlyCollection<string> values) =>
        AdminValues.Any(w => values.Contains(w, StringComparer.OrdinalIgnoreCase)) ? ConsoleRoles.Admin : null;
}

/// <summary>The OpenID Connect sign-in: authorization code with PKCE, run by the server, ending in the console's own cookie.</summary>
internal static class ConsoleOidc
{
    public const string LoginProvider = "oidc";
    public const string SignedInWithClaim = "winche:signed-in-with";
    private const string FlowCookie = "WincheConsoleOidc";
    private const string Purpose = "Winche.Console.Oidc.v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Discovery = [];

    /// <summary>The signed-in person's provider (its id), the provider's session id and subject for them.</summary>
    public const string ProviderClaim = "winche:provider";
    public const string ProviderSessionClaim = "winche:provider-sid";
    public const string ProviderSubjectClaim = "winche:provider-sub";

    /// <summary>What a sign-in carries from start to callback (and a test, its unsaved settings).</summary>
    /// <param name="LinkTo">Adding the provider to this (signed-in) console account, from its account menu: not a sign-in.</param>
    internal sealed record Flow(string State, string Nonce, string Verifier, string ReturnUrl, bool Test, ConsoleOidcSettings Settings, string? Secret, string? LinkTo = null);

    /// <summary>A provider's client secret's name with the server's secrets (Server settings → Authentication → Sign-in method).</summary>
    public static string SecretFor(string providerId) => ConsoleOidcSettings.SecretFor(providerId);

    /// <summary>Unsaved settings to try (Server settings' Test sign-in), protected for a short while.</summary>
    internal sealed record TestTicket(ConsoleOidcSettings Settings, string? Secret);

    internal static string ProtectTest(IServiceProvider sp, ConsoleOidcSettings settings, string? secret) =>
        Protector(sp).ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(new TestTicket(settings, secret)), TimeSpan.FromMinutes(10));

    private static IDataProtector Protector(IServiceProvider sp) => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    /// <summary>The provider the signed-in person came through, while it's on.</summary>
    internal static ConsoleOidcSettings? ProviderOf(ClaimsPrincipal user, ConsoleOptions options) =>
        user.FindFirstValue(ProviderClaim) is { } id ? options.ServerSettings?.ConsoleProviders.FirstOrDefault(p => p.Id == id && p.Enabled) : null;

    /// <summary>Whether the signed-in person came through a provider that vouches for their two-step sign-in.</summary>
    internal static bool ProviderTwoStep(HttpContext http, ConsoleOptions options) => ProviderOf(http.User, options) is { TrustProviderTwoStep: true };

    private static Task<OpenIdConnectConfiguration> DiscoverAsync(string issuer, CancellationToken ct)
    {
        var address = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        ConfigurationManager<OpenIdConnectConfiguration> manager;
        lock (Discovery)
            if (!Discovery.TryGetValue(address, out manager!))
                Discovery[address] = manager = new ConfigurationManager<OpenIdConnectConfiguration>(address, new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = address.StartsWith("https", StringComparison.OrdinalIgnoreCase) });
        return manager.GetConfigurationAsync(ct);
    }

    private static string B64Url(byte[] bytes) => Base64UrlEncoder.Encode(bytes);

    /// <summary>
    /// A path on this server only: starting with one '/', with no '\\', no whitespace or control character (a browser
    /// drops tabs and line breaks, turning "/&lt;tab&gt;/x" into "//x", another site), and no scheme or host.
    /// </summary>
    /// <summary>
    /// How adding a sign-in method asks the provider to show who's signing in: <c>login</c> (sign in again), or for
    /// Google, which has no <c>login</c>, <c>select_account</c> (choose the account).
    /// </summary>
    internal static string LinkPrompt(string issuer) =>
        Uri.TryCreate(issuer, UriKind.Absolute, out var u) && u.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ? "select_account" : "login";

    internal static bool LocalPath(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//") && !url.Contains('\\')
        && url.All(c => c > 0x20 && c != 0x7f)
        && Uri.TryCreate(url, UriKind.Relative, out var relative) && !relative.OriginalString.Contains("://");

    public static IEndpointRouteBuilder MapConsoleOidcEndpoints(this IEndpointRouteBuilder app, ConsoleOptions options)
    {
        var group = app.MapGroup("/session/oidc");

        // Off to the provider. A test (from Server settings) carries unsaved settings; otherwise those in force.
        group.MapGet("/start", async (HttpContext http, string? provider, string? returnUrl, string? test, bool? link, CancellationToken ct) =>
        {
            ConsoleOidcSettings? settings;
            string? secret;
            // Adding a sign-in method: only to the account signed in here (two-step where settings need it).
            string? linkTo = null;
            if (link == true)
            {
                var home = LocalPath(returnUrl) ? returnUrl! : options.ServerSettings?.ConsoleSignInReturnPath ?? "/";
                IResult Back(string why) => Finish(http, false, home, Fail(why));
                // Only from this console's own pages: another site can't start it in a signed-in browser.
                if (http.Request.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } site && site is not ("same-origin" or "none"))
                    return Back("Add a sign-in method from your account menu here.");
                // /start is anonymous: the session is read here (no default scheme fills http.User).
                if ((await http.AuthenticateAsync(ConsoleSession.Scheme)).Principal is not { } principal
                    || principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } me) return Back("Sign in first, then add it from your account menu.");
                http.User = principal;
                if (await Api.ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is not null)
                    return Back("Turn on two-step sign-in for your console account first: adding a sign-in method needs it.");
                linkTo = me;
            }
            if (test is { Length: > 0 })
            {
                TestTicket? ticket;
                try { ticket = JsonSerializer.Deserialize<TestTicket>(Protector(http.RequestServices).ToTimeLimitedDataProtector().Unprotect(test)); }
                catch (CryptographicException) { return Results.BadRequest("This test has expired: start it again."); }
                (settings, secret) = (ticket!.Settings with { Enabled = true }, ticket.Secret);
            }
            else
            {
                // The provider asked for; with none named, the only one on.
                var on = options.ServerSettings?.ConsoleProviders.Where(p => p.Enabled).ToList() ?? [];
                settings = provider is { Length: > 0 } ? on.FirstOrDefault(p => p.Id == provider) : on.Count == 1 ? on[0] : null;
                if (settings is null) return Results.NotFound();
                secret = await options.ServerSettings!.ConsoleProviderSecretAsync(settings.Id, ct);
            }
            var back = LocalPath(returnUrl) ? returnUrl! : options.ServerSettings!.ConsoleSignInReturnPath;
            OpenIdConnectConfiguration config;
            try { config = await DiscoverAsync(settings.Issuer!, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Finish(http, test is not null, back, Fail($"The provider's discovery document couldn't be read at {settings.Issuer}: {ex.GetBaseException().Message}"));
            }

            var flow = new Flow(B64Url(RandomNumberGenerator.GetBytes(24)), B64Url(RandomNumberGenerator.GetBytes(24)),
                B64Url(RandomNumberGenerator.GetBytes(32)), back, test is not null, settings, secret, linkTo);
            http.Response.Cookies.Append(FlowCookie, Protector(http.RequestServices).ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(flow), TimeSpan.FromMinutes(10)),
                new CookieOptions { Path = CookiePath(http), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(10) });
            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code", ["client_id"] = settings.ClientId!, ["redirect_uri"] = options.ServerSettings!.ConsoleSignInCallbackUrl,
                ["scope"] = settings.Scopes, ["state"] = flow.State, ["nonce"] = flow.Nonce,
                ["code_challenge"] = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier))), ["code_challenge_method"] = "S256",
            };
            // Adding a sign-in method: the provider asks who's there, so a session already open there can't finish it unseen.
            if (linkTo is not null) query["prompt"] = LinkPrompt(settings.Issuer!);
            var url = config.AuthorizationEndpoint + (config.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            return Results.Redirect(url);
        });

        // Back from the provider: the code for tokens, the ID token checked, then the account found or made and signed in.
        group.MapGet("/callback", async (HttpContext http, string? code, string? state, string? error, string? error_description, CancellationToken ct) =>
        {
            Flow? flow = null;
            if (http.Request.Cookies.TryGetValue(FlowCookie, out var raw))
                try { flow = JsonSerializer.Deserialize<Flow>(Protector(http.RequestServices).ToTimeLimitedDataProtector().Unprotect(raw)); }
                catch (CryptographicException) { }
            http.Response.Cookies.Delete(FlowCookie, new CookieOptions { Path = CookiePath(http) });
            var fallback = options.ServerSettings?.ConsoleSignInReturnPath ?? "/";
            if (flow is null || state != flow.State) return Finish(http, false, fallback, Fail("The sign-in took too long or came back to another browser: try again."));
            if (error is not null) return Finish(http, flow.Test, flow.ReturnUrl, Fail($"The provider said: {error_description ?? error}"));
            if (string.IsNullOrEmpty(code)) return Finish(http, flow.Test, flow.ReturnUrl, Fail("The provider sent no code back."));

            Outcome outcome;
            try { outcome = await CompleteAsync(http, flow, code, options, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                http.RequestServices.GetService<ILogger<ConsoleOidcLog>>()?.LogWarning(ex, "Console sign-in through the identity provider failed.");
                outcome = Fail(ex is OidcException ? ex.Message : $"The sign-in couldn't be completed: {ex.GetBaseException().Message}");
            }
            return Finish(http, flow.Test, flow.ReturnUrl, outcome);
        });
        // The signed-in account's sign-in methods (its account menu): its password, and each provider on, linked or not.
        var methods = app.MapGroup("/session/sign-in-methods").RequireAuthorization(ConsoleRoles.SignedInPolicy);
        methods.MapGet("", async (HttpContext http, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            if (await accounts.CurrentAsync(http.User, ct) is not { } me) return Results.Unauthorized();
            var on = options.ServerSettings?.ConsoleProviders.Where(p => p.Enabled).ToList() ?? [];
            return Results.Json(new
            {
                password = me.HasPassword,
                providers = on.Select(p => new { id = p.Id, label = p.Label, linked = me.Providers.Contains(p.Id) }),
            });
        });
        methods.MapDelete("/{provider}", async (string provider, HttpContext http, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            if (await accounts.CurrentAsync(http.User, ct) is not { } me) return Results.Unauthorized();
            if (await Api.ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } twoStep) return twoStep;
            try
            {
                await accounts.UseAsync(a => a.UnlinkProviderAsync(me.Id, provider, ct));
                return Results.Ok();
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Back-channel logout: the provider says someone signed out there (their session, or all of theirs), and the
        // console's sessions from it end at their next request. Register it with the provider beside the redirect URI.
        group.MapPost("/backchannel-logout", async (HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = http.Request.HasFormContentType ? (string?)(await http.Request.ReadFormAsync(ct))["logout_token"] : null;
            if (string.IsNullOrEmpty(token)) return Results.BadRequest(new { error = "invalid_request", error_description = "No logout_token." });
            var (provider, sid, sub, problem) = await ReadLogoutTokenAsync(token, options, ct);
            if (problem is not null) return Results.BadRequest(new { error = "invalid_request", error_description = problem });
            // The accounts end their sessions from it (by sid, or all of the person's); the console's own list ends the
            // sign-ins at their next request.
            try { await http.RequestServices.GetRequiredService<ConsoleAccounts>().UseAsync(a => a.EndProviderSessionsAsync(provider!, token, ct)); }
            catch (Winche.Auth.AuthException) { /* not known there (yet): the console's list still ends them */ }
            // Kept as long as any session could last.
            await http.RequestServices.GetRequiredService<ConsoleStore>().AddSignOutAsync(provider!, sid, sub, ProviderSessionIdle, ct);
            return Results.Ok();
        }).DisableAntiforgery();
        return app;
    }

    internal sealed class ConsoleOidcLog;
    private sealed class OidcException(string message) : Exception(message);

    /// <summary>What a sign-in came to: who, what the provider said, the role it maps to, or why not.</summary>
    /// <param name="SecondStep">Signed in but for their console two-step: the sign-in page asks for the code.</param>
    internal sealed record Outcome(bool Ok, string? Error, string? Email = null, string? Name = null, string? Subject = null,
        IReadOnlyList<string>? RoleValues = null, string? Role = null, JsonObject? Claims = null, bool SecondStep = false);

    private static Outcome Fail(string error) => new(false, error);

    private static async Task<Outcome> CompleteAsync(HttpContext http, Flow flow, string code, ConsoleOptions options, CancellationToken ct)
    {
        var s = flow.Settings;
        var config = await DiscoverAsync(s.Issuer!, ct);

        // The code for tokens.
        var (status, tokens) = await TokenRequestAsync(config, s, flow.Secret, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = options.ServerSettings!.ConsoleSignInCallbackUrl,
            ["client_id"] = s.ClientId!, ["code_verifier"] = flow.Verifier,
        }, ct);
        if (status is < 200 or > 299)
            throw new OidcException($"The provider refused the code: {(string?)tokens?["error_description"] ?? (string?)tokens?["error"] ?? ((HttpStatusCode)status).ToString()}. Check the client ID and secret.");
        if ((string?)tokens?["id_token"] is not { } idToken) throw new OidcException("The provider sent no ID token: are the scopes missing openid?");

        // The ID token: signed by the provider, for this client, for this sign-in.
        var check = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer, ValidAudience = s.ClientId, IssuerSigningKeys = config.SigningKeys, ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!check.IsValid) throw new OidcException($"The ID token didn't check out: {check.Exception?.Message}");
        var jwt = (JsonWebToken)check.SecurityToken;
        if (!jwt.TryGetPayloadValue<string>("nonce", out var nonce) || nonce != flow.Nonce) throw new OidcException("The ID token is for another sign-in.");

        var claims = JsonNode.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload))!.AsObject();
        // Some providers put email or groups only in userinfo.
        if ((!claims.ContainsKey("email") || (s.RoleClaim is { Length: > 0 } && Path(claims, s.RoleClaim) is null))
            && !string.IsNullOrEmpty(config.UserInfoEndpoint) && (string?)tokens!["access_token"] is { } access)
        {
            using var info = new HttpRequestMessage(HttpMethod.Get, config.UserInfoEndpoint);
            info.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var infoResponse = await Http.SendAsync(info, ct);
            if (infoResponse.IsSuccessStatusCode && JsonNode.Parse(await infoResponse.Content.ReadAsStringAsync(ct)) is JsonObject extra)
                foreach (var (k, v) in extra)
                    if (!claims.ContainsKey(k)) claims[k] = v?.DeepClone();
        }

        // Keycloak and others carry roles only in the access token. When it's a JWT this provider signed (and still
        // current), its role claim is as trustworthy as the ID token's.
        if (s.RoleClaim is { Length: > 0 } && Path(claims, s.RoleClaim) is null && (string?)tokens!["access_token"] is { } accessToken
            && new JsonWebTokenHandler().CanReadToken(accessToken))
        {
            var at = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidIssuer = config.Issuer, ValidateAudience = false, IssuerSigningKeys = config.SigningKeys, ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            });
            if (at.IsValid && Path(JsonNode.Parse(Base64UrlEncoder.Decode(((JsonWebToken)at.SecurityToken).EncodedPayload))!.AsObject(), s.RoleClaim) is { } fromAccess)
                claims[s.RoleClaim] = fromAccess.DeepClone();
        }

        var subject = (string?)claims["sub"] ?? throw new OidcException("The ID token has no subject.");
        var email = (string?)claims["email"];
        var verified = claims["email_verified"] is JsonValue ev && (ev.TryGetValue<bool>(out var b) ? b : (string?)ev == "true");
        var roleValues = s.RoleClaim is { Length: > 0 } ? Values(Path(claims, s.RoleClaim)) : [];
        var role = s.RoleClaim is { Length: > 0 } ? s.RoleFor(roleValues) : null;
        var name = (string?)claims["name"] ?? string.Join(' ', new[] { (string?)claims["given_name"], (string?)claims["family_name"] }.Where(x => !string.IsNullOrEmpty(x)));
        var outcome = new Outcome(true, null, email, name, subject, roleValues, role, claims);
        if (flow.Test) return outcome;

        // The account, through the console's accounts (this provider is one of their Sign-in method providers): the one
        // linked to this provider account; else one with the same, verified, email (linked now); else a new one.
        var accounts = http.RequestServices.GetRequiredService<ConsoleAccounts>();
        var server = options.ServerSettings!;
        await accounts.MirrorProvidersAsync(server.ConsoleProviders, (id, c) => server.Secrets.GetAsync(ConsoleOidcSettings.KeycloakSyncSecretFor(id), c), s.Id, ct,
            async (id, c) => (await server.Secrets.InfoAsync(ConsoleOidcSettings.KeycloakSyncSecretFor(id), c))?.Reference);
        var linked = await accounts.UseAsync(a => a.FindByProviderAsync(s.Id, subject, ct));
        if (flow.LinkTo is { } owner)
        {
            // Still the account that asked: someone else signed in here meanwhile gets nothing linked.
            if ((await http.AuthenticateAsync(ConsoleSession.Scheme)).Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != owner)
                throw new OidcException("You're signed in as someone else now: add the sign-in method again from your account menu.");
            // From the account menu: the signed-in owner adds it, whatever the address. Their session stays as it is.
            if (linked is not null && linked.Uid != owner) throw new OidcException("That account at the provider already signs in to another console account.");
            if (linked is null)
            {
                try
                {
                    var made = await accounts.UseAsync(a => a.LinkProviderAsync(owner, s.Id, idToken, flow.Nonce, Api.AuthEndpoints.Info(http), ct));
                    await accounts.UseAsync(a => a.SignOutAsync(made.RefreshToken, ct));   // only the link was wanted, not another session
                }
                catch (Winche.Auth.AuthException ex) when (ex.Code == Winche.Auth.AuthErrorCodes.MfaRequired) { }   // linked; its sign-in would ask for their second step
                catch (Winche.Auth.AuthException ex) { throw new OidcException(ex.Message); }
            }
            return outcome;
        }
        ConsoleAccount? byEmail = null;
        if (linked is null)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new OidcException("The provider didn't share an email address: add the email scope.");
            byEmail = await accounts.FindByEmailAsync(email, ct);
            if (byEmail is not null && !verified)
                throw new OidcException($"There's already a console account for {email}, and the provider doesn't say the address is verified.");
            // Never by the address into an account someone has a password for or has signed in to (invites make theirs with a
            // password, so in practice never): its owner links it, signed in, from their account menu. Otherwise whoever had
            // that address at the provider would be let in (and a sign-in method the owner removed would come straight back).
            if (byEmail is { LastSignInAt: not null } or { HasPassword: true })
                throw new OidcException($"There's already a console account for {email}: sign in the usual way, then add {s.Label} from your account menu (Sign-in methods).");
            if (byEmail is null && !s.CreateAccounts) throw new OidcException($"There's no console account for {email}: ask an admin to add you.");
        }
        Winche.Auth.AuthTokens session;
        try
        {
            session = byEmail is not null
                ? await accounts.UseAsync(a => a.LinkProviderAsync(byEmail.Id, s.Id, idToken, flow.Nonce, Api.AuthEndpoints.Info(http), ct))
                : await accounts.UseAsync(a => a.SignInWithProviderAsync(s.Id, idToken, flow.Nonce, Api.AuthEndpoints.Info(http), ct));
        }
        catch (Winche.Auth.AuthException ex) when (ex.Code == Winche.Auth.AuthErrorCodes.MfaRequired && ex.Details?.GetValueOrDefault("mfaToken") is string mfa)
        {
            // Their console two-step, which this provider doesn't stand in for: the sign-in page asks for the code.
            Api.AuthEndpoints.AwaitSecondStep(http, mfa);
            return outcome with { SecondStep = true };
        }
        catch (Winche.Auth.AuthException ex) { throw new OidcException(ex.Code == Winche.Auth.AuthErrorCodes.UserDisabled ? "This console account is turned off." : ex.Message); }
        var user = ConsoleAccounts.From(session.User);
        if (user.FirstName is null && user.LastName is null && ((string?)claims["given_name"] is not null || (string?)claims["family_name"] is not null))
            user = await accounts.UpdateAsync(user.Id, (string?)claims["given_name"] ?? "", (string?)claims["family_name"] ?? "", ct: ct);   // a new account: their name

        // Roles from the provider, when it gives them. A built-in admin (with a password) stays admin: the way back in.
        if (s.RoleClaim is { Length: > 0 })
        {
            var wanted = user.HasPassword && user.IsAdmin ? ConsoleRoles.Admin : role;
            if (wanted != user.Role) user = await accounts.UpdateAsync(user.Id, role: wanted, clearRole: wanted is null, ct: ct);
        }

        // The console's session follows the provider's: its refresh token is kept (in the encrypted cookie) and used every
        // few minutes; when the provider stops renewing it, or signs the person out, the console's session ends too.
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now + ProviderSessionIdle };
        properties.Items[SignedInAtItem] = now.ToString("O");
        properties.Items[CheckAtItem] = (now + CheckEvery).ToString("O");
        if ((string?)tokens!["refresh_token"] is { Length: > 0 } refresh)
            properties.StoreTokens([new AuthenticationToken { Name = "refresh_token", Value = refresh }]);
        else
            properties.Items[UntilItem] = (now + WithoutRefresh).ToString("O");   // nothing to follow it with: back through the provider in a while
        List<Claim> mine = [new(SignedInWithClaim, LoginProvider), new(ProviderClaim, s.Id), new(ProviderSubjectClaim, subject)];
        if ((string?)claims["sid"] is { Length: > 0 } sid) mine.Add(new(ProviderSessionClaim, sid));
        await ConsoleSession.SignInAsync(http, session, mine, properties, user);   // as the account is now: its name and role just set
        return outcome;
    }

    /// <summary>How often a provider session is checked with the provider (its refresh token).</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A provider gave no refresh token: the console's session lasts this long, then it's back through the provider.</summary>
    internal static readonly TimeSpan WithoutRefresh = TimeSpan.FromHours(1);

    /// <summary>The longest a provider session goes unused (the provider's own usually ends sooner).</summary>
    internal static readonly TimeSpan ProviderSessionIdle = TimeSpan.FromDays(30);

    private const string SignedInAtItem = "winche:signed-in-at";
    private const string CheckAtItem = "winche:check-at";
    private const string UntilItem = "winche:until";

    /// <summary>A request to the provider's token endpoint (client secret by Basic, unless it only takes it in the body).</summary>
    private static async Task<(int Status, JsonObject? Body)> TokenRequestAsync(OpenIdConnectConfiguration config, ConsoleOidcSettings s, string? secret,
        Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenEndpoint);
        if (!string.IsNullOrEmpty(secret))
        {
            var methods = config.TokenEndpointAuthMethodsSupported;
            if (methods.Count > 0 && !methods.Contains("client_secret_basic") && methods.Contains("client_secret_post")) form["client_secret"] = secret;
            else request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(s.ClientId!)}:{Uri.EscapeDataString(secret)}")));
        }
        request.Content = new FormUrlEncodedContent(form);
        using var response = await Http.SendAsync(request, ct);
        JsonObject? body = null;
        try { body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject; } catch (JsonException) { }
        return ((int)response.StatusCode, body);
    }

    private enum Renewal { Renewed, Refused, Unreachable }

    /// <summary>Renewals under way or just done, by refresh token: parallel requests share one (providers may rotate them).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, Task<(Renewal, string?)> Task)> Renewals = new();

    private static Task<(Renewal, string?)> RenewAsync(ConsoleOidcSettings s, string? secret, string refreshToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in Renewals)
            if (now - v.At > TimeSpan.FromMinutes(1)) Renewals.TryRemove(k, out _);
        return Renewals.GetOrAdd(refreshToken, _ => (now, Run())).Task;

        async Task<(Renewal, string?)> Run()
        {
            try
            {
                var config = await DiscoverAsync(s.Issuer!, CancellationToken.None);
                var (status, body) = await TokenRequestAsync(config, s, secret, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = s.ClientId!,
                }, CancellationToken.None);
                if (status is >= 200 and <= 299) return (Renewal.Renewed, (string?)body?["refresh_token"] ?? refreshToken);
                // The provider answered no: signed out there, session over, account turned off, or the client changed.
                return status is 400 or 401 ? (Renewal.Refused, null) : (Renewal.Unreachable, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException) { return (Renewal.Unreachable, null); }
        }
    }

    /// <summary>
    /// Every request on a provider session: ended when its provider is gone or off, signed out there (back-channel), past
    /// its time, or — checked every few minutes — when the provider won't renew it. A password session isn't touched.
    /// </summary>
    internal static async Task ValidateSessionAsync(CookieValidatePrincipalContext ctx, ConsoleOptions options)
    {
        var principal = ctx.Principal;
        if (principal?.FindFirstValue(ProviderClaim) is not { } id || options.ServerSettings is null) return;
        var http = ctx.HttpContext;
        async Task EndAsync(string why)
        {
            http.RequestServices.GetService<ILogger<ConsoleOidcLog>>()?.LogInformation(
                "Console session of {Email} ended: {Why}.", principal.FindFirstValue(ClaimTypes.Email), why);
            await ConsoleSession.EndAsync(ctx);   // the accounts' session too
        }
        static DateTimeOffset? At(AuthenticationProperties p, string item) =>
            p.Items.TryGetValue(item, out var v) && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

        var provider = options.ServerSettings.ConsoleProviders.FirstOrDefault(p => p.Id == id && p.Enabled);
        if (provider is null) { await EndAsync("its identity provider was removed or turned off"); return; }
        var now = DateTimeOffset.UtcNow;
        var props = ctx.Properties;
        if (At(props, UntilItem) is { } until && now > until) { await EndAsync("it's time to sign in through the provider again"); return; }

        var sid = principal.FindFirstValue(ProviderSessionClaim);
        var sub = principal.FindFirstValue(ProviderSubjectClaim);
        var since = At(props, SignedInAtItem) ?? DateTimeOffset.MinValue;
        if (await http.RequestServices.GetRequiredService<ConsoleStore>().SignedOutAsync(id, sid, sub, since, http.RequestAborted))
        { await EndAsync("signed out at the identity provider"); return; }

        if (At(props, CheckAtItem) is { } checkAt && now < checkAt) return;
        if (props.GetTokenValue("refresh_token") is not { } refresh) return;
        var (renewal, next) = await RenewAsync(provider, await options.ServerSettings.ConsoleProviderSecretAsync(id, http.RequestAborted), refresh);
        switch (renewal)
        {
            case Renewal.Refused:
                await EndAsync("the identity provider no longer renews it");
                return;
            case Renewal.Renewed:
                props.UpdateTokenValue("refresh_token", next!);
                props.Items[CheckAtItem] = (now + CheckEvery).ToString("O");
                break;
            default:   // the provider can't be reached: carry on, and ask again soon
                props.Items[CheckAtItem] = (now + TimeSpan.FromMinutes(1)).ToString("O");
                break;
        }
        ctx.ShouldRenew = true;
    }

    /// <summary>A back-channel logout token checked: from one of the console's providers, for it, about a sign-out.</summary>
    private static async Task<(string? Provider, string? Sid, string? Sub, string? Problem)> ReadLogoutTokenAsync(string token, ConsoleOptions options, CancellationToken ct)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(token)) return (null, null, null, "The logout token isn't a JWT.");
        var issuer = handler.ReadJsonWebToken(token).Issuer;
        foreach (var p in options.ServerSettings?.ConsoleProviders.Where(p => p.Enabled) ?? [])
        {
            OpenIdConnectConfiguration config;
            try { config = await DiscoverAsync(p.Issuer!, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            if (config.Issuer != issuer) continue;
            var check = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = config.Issuer, ValidAudience = p.ClientId, IssuerSigningKeys = config.SigningKeys,
                ValidateLifetime = false, RequireExpirationTime = false,   // logout tokens may carry no exp; iat is checked below
            });
            if (!check.IsValid) continue;   // maybe another client at the same issuer
            var jwt = (JsonWebToken)check.SecurityToken;
            var payload = JsonNode.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload))!.AsObject();
            if (payload["events"] is not JsonObject events || !events.ContainsKey("http://schemas.openid.net/event/backchannel-logout"))
                return (null, null, null, "The token isn't a logout token.");
            if (payload.ContainsKey("nonce")) return (null, null, null, "A logout token has no nonce.");
            if (jwt.IssuedAt < DateTime.UtcNow.AddMinutes(-10) || jwt.IssuedAt > DateTime.UtcNow.AddMinutes(5))
                return (null, null, null, "The logout token is too old.");
            var sid = (string?)payload["sid"];
            var sub = (string?)payload["sub"];
            if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(sub)) return (null, null, null, "The logout token names no session or person.");
            return (p.Id, sid, sub, null);
        }
        return (null, null, null, "The logout token isn't from one of the console's identity providers.");
    }

    /// <summary>A claim by name, or a dotted path into objects (realm_access.roles).</summary>
    private static JsonNode? Path(JsonObject claims, string path)
    {
        if (claims.TryGetPropertyValue(path, out var direct)) return direct;
        JsonNode? node = claims;
        foreach (var part in path.Split('.'))
            node = node is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
        return node;
    }

    private static List<string> Values(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x))!],
        JsonValue v when v.ToString() is { Length: > 0 } t => [.. t.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)],
        _ => [],
    };

    /// <summary>
    /// A test reports back to the page that opened it (and shows what came back); a sign-in goes back to the console,
    /// with the reason when it failed.
    /// </summary>
    private static IResult Finish(HttpContext http, bool test, string returnUrl, Outcome outcome)
    {
        if (!test)
            return Results.Redirect(!outcome.Ok ? $"{returnUrl}{(returnUrl.Contains('?') ? "&" : "?")}signInError={Uri.EscapeDataString(outcome.Error!)}"
                : outcome.SecondStep ? $"{returnUrl}{(returnUrl.Contains('?') ? "&" : "?")}secondStep=1" : returnUrl);
        var json = JsonSerializer.Serialize(new
        {
            type = "winche-oidc-test", ok = outcome.Ok, error = outcome.Error, email = outcome.Email, name = outcome.Name, subject = outcome.Subject,
            roleValues = outcome.RoleValues, role = outcome.Role, claims = outcome.Claims,
        });
        var safe = json.Replace("<", "\\u003c");
        var html = $$"""
            <!doctype html><meta charset="utf-8"><title>Test sign-in</title>
            <body style="font-family:system-ui;padding:24px">
            <p>{{WebUtility.HtmlEncode(outcome.Ok ? "The sign-in worked. You can close this window." : outcome.Error)}}</p>
            <script>
              const result = {{safe}};
              if (window.opener) { window.opener.postMessage(result, location.origin); window.close(); }
            </script>
            """;
        return Results.Content(html, "text/html");
    }
}
