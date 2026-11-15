using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth;

namespace Winche.Console.Identity;

/// <summary>
/// A console sign-in: the console's cookie holds a session of the console accounts' Winche Auth (its refresh token, in the
/// encrypted cookie). Every few minutes the session is renewed there, so an account turned off, deleted or signed out
/// everywhere (Server settings → Authentication → Users) ends its console sign-ins, and a changed role applies.
/// </summary>
internal static class ConsoleSession
{
    public const string Scheme = "Identity.Application";   // the cookie scheme's name (kept from before)
    private const string RefreshItem = "account:refresh", CheckAtItem = "account:check-at";

    /// <summary>How often the session is renewed with the accounts.</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Between renewals each request looks at the account (at most this old): one turned off or deleted is signed out
    /// within seconds, and a changed role applies.
    /// </summary>
    internal static TimeSpan AccountFreshFor { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ConsoleAccount? Account, DateTimeOffset At)> Accounts = new();

    /// <summary>Forgets what's known of an account, so its next request looks again (a change made here).</summary>
    internal static void Forget(string accountId) => Accounts.TryRemove(accountId, out _);

    /// <summary>The cookie's claims: who (uid, email, name) and the account's own role; other claims (a provider's) kept as given.</summary>
    public static ClaimsPrincipal Principal(ConsoleAccount account, IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Name, account.Email ?? account.Id) };
        if (account.Email is { } email) claims.Add(new Claim(ClaimTypes.Email, email));
        if (account.Role is { } role) claims.Add(new Claim(ClaimTypes.Role, role));
        if (extra is not null) claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>
    /// Signs <paramref name="tokens"/>' account in to the console (a persistent cookie): as <paramref name="account"/> when
    /// it was changed since the tokens were made (a provider's sign-in gives it its name and role after).
    /// </summary>
    public static async Task SignInAsync(HttpContext http, AuthTokens tokens, IEnumerable<Claim>? extra = null, AuthenticationProperties? properties = null,
        ConsoleAccount? account = null)
    {
        account ??= ConsoleAccounts.From(tokens.User);
        Forget(account.Id);   // what was known of it is older than this sign-in (a provider may just have changed its role)
        properties ??= new AuthenticationProperties();
        properties.IsPersistent = true;
        properties.Items[RefreshItem] = tokens.RefreshToken;
        properties.Items[CheckAtItem] = (DateTimeOffset.UtcNow + CheckEvery).ToString("O");
        await http.SignInAsync(Scheme, Principal(account, extra), properties);
    }

    /// <summary>Ends this console sign-in, and its session with the accounts.</summary>
    public static async Task SignOutAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(Scheme);
        if (result.Properties?.Items.TryGetValue(RefreshItem, out var refresh) == true && refresh is { Length: > 0 })
        {
            var accounts = http.RequestServices.GetRequiredService<ConsoleAccounts>();
            try { await accounts.UseAsync(a => a.SignOutAsync(refresh, http.RequestAborted)); }
            catch (AuthException) { /* already over */ }
        }
        await http.SignOutAsync(Scheme);
    }

    /// <summary>Every request with the cookie: renewed with the accounts every few minutes; refused there, it ends.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var props = ctx.Properties;
        if (!props.Items.TryGetValue(RefreshItem, out var refresh) || string.IsNullOrEmpty(refresh)) { await RejectAsync(ctx); return; }
        if (props.Items.TryGetValue(CheckAtItem, out var at) && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkAt)
            && DateTimeOffset.UtcNow < checkAt)
        {
            // Not due for renewal: still, the account as it is now.
            if (ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is not { } id) return;
            var now = DateTimeOffset.UtcNow;
            if (!Accounts.TryGetValue(id, out var known) || now - known.At > AccountFreshFor)
            {
                var found = await ctx.HttpContext.RequestServices.GetRequiredService<ConsoleAccounts>().FindAsync(id, ctx.HttpContext.RequestAborted);
                known = (found, now);
                Accounts[id] = known;
            }
            if (known.Account is not { Active: true } account) { await EndAsync(ctx); return; }   // turned off or deleted
            if (account.Role != ctx.Principal.FindFirstValue(ClaimTypes.Role))
            {
                var providerClaims = ctx.Principal.Claims.Where(c => c.Type.StartsWith("winche:", StringComparison.Ordinal)).ToList();
                ctx.ReplacePrincipal(Principal(account, providerClaims));
                ctx.ShouldRenew = true;
            }
            return;
        }

        var accounts = ctx.HttpContext.RequestServices.GetRequiredService<ConsoleAccounts>();
        AuthTokens tokens;
        try { tokens = await RenewAsync(accounts, refresh); }
        catch (AuthException) { await RejectAsync(ctx); return; }   // turned off, deleted, signed out everywhere, or the session ran out

        props.Items[RefreshItem] = tokens.RefreshToken;
        props.Items[CheckAtItem] = (DateTimeOffset.UtcNow + CheckEvery).ToString("O");
        // The account as it is now (its role may have changed); the provider's claims stay.
        var kept = ctx.Principal?.Claims.Where(c => c.Type.StartsWith("winche:", StringComparison.Ordinal)).ToList();
        ctx.ReplacePrincipal(Principal(ConsoleAccounts.From(tokens.User), kept));
        ctx.ShouldRenew = true;
    }

    /// <summary>
    /// Renewals under way or just made, by the refresh token they used. A page asks for several things at once, each with
    /// the same cookie: they share one renewal (and so one new session), where each renewing on its own would leave all
    /// but the first with a refresh token already used, and sign the person out.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Lazy<Task<AuthTokens>> Renewal, DateTimeOffset At)> Renewals = new();

    /// <summary>How long a renewal is shared: requests already on their way with the old cookie.</summary>
    private static readonly TimeSpan ShareFor = TimeSpan.FromMinutes(1);

    private static Task<AuthTokens> RenewAsync(ConsoleAccounts accounts, string refresh)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, old) in Renewals)
            if (now - old.At > ShareFor) Renewals.TryRemove(key, out _);
        // Not the request's cancellation: the others share it.
        // Lazy: GetOrAdd may make two entries at once, but only the one kept ever renews.
        return Renewals.GetOrAdd(refresh, r => (new Lazy<Task<AuthTokens>>(() => accounts.UseAsync(a => a.RefreshAsync(r, AuthRequestInfo.Server, CancellationToken.None))), now)).Renewal.Value;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(Scheme);
    }

    /// <summary>
    /// Ends the sign-in being checked (its cookie, and its session with the accounts): from inside the cookie's own check,
    /// so the refresh token is read from it, not by authenticating again.
    /// </summary>
    public static async Task EndAsync(CookieValidatePrincipalContext ctx)
    {
        if (ctx.Properties.Items.TryGetValue(RefreshItem, out var refresh) && refresh is { Length: > 0 })
        {
            var accounts = ctx.HttpContext.RequestServices.GetRequiredService<ConsoleAccounts>();
            try { await accounts.UseAsync(a => a.SignOutAsync(refresh, ctx.HttpContext.RequestAborted)); }
            catch (AuthException) { /* already over */ }
        }
        await RejectAsync(ctx);
    }
}
