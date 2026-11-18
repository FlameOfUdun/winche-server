using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Console.Options;

namespace Winche.Console.Identity;

/// <summary>Marks an endpoint a service account with deploy scope <see cref="Scope"/> may call (they can call nothing else).</summary>
public sealed record ConsoleCiScope(string Scope)
{
    public const string Functions = "functions", Rules = "rules", Indexes = "indexes", Assets = "assets";
    public static readonly string[] All = [Functions, Rules, Indexes, Assets];
}

/// <summary>
/// The one-time codes of the CLI's sign-in, in the project's config database (<c>AddWincheConfig</c>). The CI tokens
/// it kept before (<c>wct_…</c>) became service accounts' keys (<see cref="ServiceAccountStore"/>).
/// </summary>
public sealed class ConsoleTokenStore
{
    public const string Prefix = "wct_";
    internal const string Tokens = "winche_console_tokens", Codes = "winche_console_cli_codes", Devices = "winche_console_cli_devices";

    private static readonly string[] Schema =
    [
        $"""
        CREATE TABLE IF NOT EXISTS {Tokens} (
            id           text        PRIMARY KEY,
            kind         text        NOT NULL,
            name         text        NOT NULL,
            user_id      text        NOT NULL,
            user_email   text        NULL,
            role         text        NOT NULL,
            scopes       text[]      NOT NULL,
            hash         bytea       NOT NULL,
            created_at   timestamptz NOT NULL,
            expires_at   timestamptz NOT NULL,
            last_used_at timestamptz NULL,
            revoked_at   timestamptz NULL
        );
        CREATE TABLE IF NOT EXISTS {Codes} (
            hash       bytea       PRIMARY KEY,
            challenge  text        NOT NULL,
            user_id    text        NOT NULL,
            user_email text        NULL,
            role       text        NOT NULL,
            name       text        NOT NULL,
            expires_at timestamptz NOT NULL
        );
        """,
        // winche login --no-browser: a code to type in a browser anywhere (RFC 8628's device flow, ending in the same PKCE exchange).
        $"""
        CREATE TABLE IF NOT EXISTS {Devices} (
            hash       bytea       PRIMARY KEY,
            user_code  text        NOT NULL UNIQUE,
            challenge  text        NOT NULL,
            name       text        NOT NULL,
            expires_at timestamptz NOT NULL,
            code       text        NULL
        );
        """,
    ];

    private readonly NpgsqlDataSource? _db;
    private readonly TimeProvider _time;
    private bool _ready;

    public ConsoleTokenStore(IServiceProvider services, TimeProvider? time = null)
    {
        _db = services.GetKeyedService<NpgsqlDataSource>("WincheConfigOptions");
        _time = time ?? TimeProvider.System;
    }

    /// <summary>False without <c>AddWincheConfig</c>: there's nowhere to keep tokens.</summary>
    public bool Available => _db is not null;


    private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Console tokens need AddWincheConfig(…).");

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ready) return;
        await Winche.Core.WincheSchema.EnsureAsync(Db, "console-tokens", Schema, ct);
        _ready = true;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static string Random(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>A one-time code (5 minutes) for the CLI's sign-in, redeemed with the PKCE verifier of <paramref name="challenge"/>.</summary>
    public async Task<string> CreateCodeAsync(string challenge, string userId, string? email, string role, string name, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var code = Random(24);
        await using (var sweep = Db.CreateCommand($"DELETE FROM {Codes} WHERE expires_at < now()")) await sweep.ExecuteNonQueryAsync(ct);
        await using var cmd = Db.CreateCommand(
            $"INSERT INTO {Codes} (hash, challenge, user_id, user_email, role, name, expires_at) VALUES ($1, $2, $3, $4, $5, $6, $7)");
        foreach (var p in new object?[] { Hash(code), challenge, userId, email, role, name, _time.GetUtcNow() + TimeSpan.FromMinutes(5) })
            cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        return code;
    }

    /// <summary>The sign-in <paramref name="code"/> is for (once), if <paramref name="verifier"/> matches its challenge.</summary>
    public async Task<(string UserId, string? Email, string Role, string Name)?> RedeemCodeAsync(string code, string verifier, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"""
            DELETE FROM {Codes} WHERE hash = $1 AND expires_at > now() RETURNING challenge, user_id, user_email, role, name
            """);
        cmd.Parameters.AddWithValue(Hash(code));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var expected = r.GetString(0);
        var actual = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual))) return null;
        return (r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4));
    }

    /// <summary>How long a device sign-in waits to be allowed.</summary>
    public static readonly TimeSpan DeviceLifetime = TimeSpan.FromMinutes(10);
    private const string UserCodeLetters = "BCDFGHJKLMNPQRSTVWXZ";   // no vowels (no words), nothing easily misread

    /// <summary>A device sign-in: the secret the CLI polls with, and the code its user types in the browser (<c>BCDF-GHJK</c>).</summary>
    public async Task<(string DeviceCode, string UserCode)> CreateDeviceAsync(string challenge, string name, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using (var sweep = Db.CreateCommand($"DELETE FROM {Devices} WHERE expires_at < now()")) await sweep.ExecuteNonQueryAsync(ct);
        var device = Random(24);
        for (var attempt = 0; ; attempt++)
        {
            var letters = new string(Enumerable.Range(0, 8).Select(_ => UserCodeLetters[RandomNumberGenerator.GetInt32(UserCodeLetters.Length)]).ToArray());
            var userCode = $"{letters[..4]}-{letters[4..]}";
            await using var cmd = Db.CreateCommand($"INSERT INTO {Devices} (hash, user_code, challenge, name, expires_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (user_code) DO NOTHING");
            foreach (var p in new object[] { Hash(device), userCode, challenge, name, _time.GetUtcNow() + DeviceLifetime })
                cmd.Parameters.Add(new NpgsqlParameter { Value = p });
            if (await cmd.ExecuteNonQueryAsync(ct) == 1) return (device, userCode);
            if (attempt > 5) throw new InvalidOperationException("Couldn't make a sign-in code.");
        }
    }

    internal static string NormalizeUserCode(string? code)
    {
        var letters = new string((code ?? "").ToUpperInvariant().Where(char.IsLetter).ToArray());
        return letters.Length == 8 ? $"{letters[..4]}-{letters[4..]}" : "";
    }

    /// <summary>The name of the CLI waiting with <paramref name="userCode"/>, or null (wrong, expired, already allowed).</summary>
    public async Task<string?> DeviceNameAsync(string userCode, CancellationToken ct = default)
    {
        if (_db is null) return null;
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"SELECT name FROM {Devices} WHERE user_code = $1 AND expires_at > now() AND code IS NULL");
        cmd.Parameters.AddWithValue(NormalizeUserCode(userCode));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Allows the CLI waiting with <paramref name="userCode"/> as this user; false when there's no such sign-in waiting.</summary>
    public async Task<bool> ApproveDeviceAsync(string userCode, string userId, string? email, string role, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        string challenge, name;
        await using (var find = Db.CreateCommand($"SELECT challenge, name FROM {Devices} WHERE user_code = $1 AND expires_at > now() AND code IS NULL"))
        {
            find.Parameters.AddWithValue(NormalizeUserCode(userCode));
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            (challenge, name) = (r.GetString(0), r.GetString(1));
        }
        var code = await CreateCodeAsync(challenge, userId, email, role, name, ct);
        await using var set = Db.CreateCommand($"UPDATE {Devices} SET code = $2 WHERE user_code = $1 AND code IS NULL");
        set.Parameters.AddWithValue(NormalizeUserCode(userCode));
        set.Parameters.AddWithValue(code);
        return await set.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>The CLI's poll: "pending", "expired", or the one-time code to trade (once) with its PKCE verifier.</summary>
    public async Task<(string Status, string? Code)> PollDeviceAsync(string deviceCode, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"""
            WITH d AS (SELECT hash, code, expires_at FROM {Devices} WHERE hash = $1),
                 gone AS (DELETE FROM {Devices} WHERE hash = $1 AND code IS NOT NULL RETURNING code)
            SELECT d.code, d.expires_at > now() FROM d
            """);
        cmd.Parameters.AddWithValue(Hash(deviceCode));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return ("expired", null);
        if (!r.IsDBNull(0)) return ("approved", r.GetString(0));
        return r.GetBoolean(1) ? ("pending", null) : ("expired", null);
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Signs in requests carrying a bearer token: the CLI's sign-in (<c>winche login</c>: an access token of a session in the
/// console's accounts, renewed with its refresh token), which acts as its account with its role here now; or a service
/// account's access token (<c>wsa_…</c>), which acts as the account.
/// </summary>
internal sealed class ConsoleTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "WincheConsoleToken";
    public const string TokenIdClaim = "winche_token", TokenKindClaim = "winche_token_kind";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var raw = header[7..].Trim();
        if (raw.StartsWith(ServiceAccountTokens.Prefix, StringComparison.Ordinal)) return await ServiceAccountAsync(raw);
        if (raw.StartsWith(ConsoleTokenStore.Prefix, StringComparison.Ordinal) || raw.StartsWith(ServiceAccountStore.KeyPrefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail("That's a service account's key: exchange it for an access token first (service-accounts/token; the CLI and @winche/admin do it).");
        return raw.Count(c => c == '.') == 2 ? await SessionAsync(raw) : AuthenticateResult.NoResult();
    }

    public const string ServiceAccountClaim = "winche_service_account";

    /// <summary>
    /// A service account's access token: it acts as the account, with its role; an account with deploy scopes instead
    /// acts as an admin on the endpoints marked with one of them only. Refused once its key or identity is gone or the
    /// account is turned off (looked up at most every few seconds).
    /// </summary>
    private async Task<AuthenticateResult> ServiceAccountAsync(string raw)
    {
        var tokens = Context.RequestServices.GetRequiredService<ServiceAccountTokens>();
        if (tokens.Read(raw, Api.ServiceAccountEndpoints.ApiOf(Context)) is not var (accountId, credential))
            return AuthenticateResult.Fail("That access token isn't valid here (expired, or another project's): exchange the key again.");
        var store = Context.RequestServices.GetRequiredService<ServiceAccountStore>();
        if (await LiveAccountAsync(store, accountId, credential, Context.RequestAborted) is not { } account)
            return AuthenticateResult.Fail("The service account is turned off, or its key or identity was removed.");
        var role = account.Role ?? ConsoleRoles.Admin;
        if (account.Role is null)
        {
            var allowed = Context.GetEndpoint()?.Metadata.GetOrderedMetadata<ConsoleCiScope>() ?? [];
            if (!allowed.Any(s => account.Scopes.Contains(s.Scope)))
                return AuthenticateResult.Fail($"The service account '{account.Name}' can only deploy ({string.Join(", ", account.Scopes)}).");
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "service:" + account.Id),
            new(TokenKindClaim, "service"),
            new(ServiceAccountClaim, account.Id),
            new(ClaimTypes.Role, role),
            new(Project.ProjectRoleClaims.DoneClaim, role),
            // The history names it: "billing-server (service account)".
            new(ClaimTypes.Name, $"{account.Name} (service account)"),
        };
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role)), Scheme));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, ServiceAccount Account)> LiveAccounts = new();

    private static async Task<ServiceAccount?> LiveAccountAsync(ServiceAccountStore store, string accountId, string credential, CancellationToken ct)
    {
        var key = accountId + "/" + credential;
        var now = DateTimeOffset.UtcNow;
        if (LiveAccounts.TryGetValue(key, out var seen) && now - seen.At < SessionFreshFor) return seen.Account;
        var account = await store.LiveAsync(accountId, credential, ct);
        if (account is null) LiveAccounts.TryRemove(key, out _); else LiveAccounts[key] = (now, account);
        if (LiveAccounts.Count > 10_000) foreach (var old in LiveAccounts.Where(e => now - e.Value.At > SessionFreshFor).Select(e => e.Key).ToList()) LiveAccounts.TryRemove(old, out _);
        return account;
    }

    /// <summary>The CLI's sign-in: an access token of the account's session in the console's accounts.</summary>
    private async Task<AuthenticateResult> SessionAsync(string raw)
    {
        var console = Context.RequestServices.GetRequiredService<ConsoleOptions>();
        if (console.Accounts is not { } host) return AuthenticateResult.NoResult();
        var keys = host.Services.GetRequiredService<Winche.Auth.Tokens.SigningKeyStore>();
        var auth = host.Services.GetRequiredService<Winche.Auth.WincheAuthOptions>();
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        string? kid;
        try { kid = handler.ReadJsonWebToken(raw).Kid; }
        catch (ArgumentException) { return AuthenticateResult.Fail("That isn't a console sign-in."); }
        await keys.KnowAsync(kid, Context.RequestAborted);
        var checkedToken = await handler.ValidateTokenAsync(raw, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = auth.Issuer, ValidAudience = string.IsNullOrEmpty(auth.Audience) ? auth.Issuer : auth.Audience, IssuerSigningKeys = keys.ValidationKeys(kid),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!checkedToken.IsValid || checkedToken.Claims.TryGetValue("sub", out var sub) is false || sub is not string uid)
            return AuthenticateResult.Fail("Your sign-in has ended or isn't valid here: run winche login.");

        var accounts = Context.RequestServices.GetRequiredService<ConsoleAccounts>();
        if (await accounts.FindAsync(uid, Context.RequestAborted) is not { Active: true } user)
            return AuthenticateResult.Fail("The account is no longer in the console.");
        // Its session must still be live (signed out, everywhere too, or ended by an admin: refused within seconds,
        // like the browser's sign-in, not when the access token runs out).
        if (!checkedToken.Claims.TryGetValue("sid", out var sidClaim) || sidClaim is not string sid
            || !await SessionLiveAsync(accounts, uid, sid, Context.RequestAborted))
            return AuthenticateResult.Fail("Your sign-in has ended: run winche login.");
        // The account's own role, like the browser's sign-in: in a created project, its membership's role comes from
        // ProjectRoleClaims (none, not a member there, still signs in, for what any account may do: list its projects).
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id), new(TokenKindClaim, "session"), new(ClaimTypes.Name, user.Email ?? user.Id),
            new(ConsoleSession.SessionClaim, sid),
        };
        if (!string.IsNullOrEmpty(user.Role)) claims.Add(new Claim(ClaimTypes.Role, user.Role));
        if (user.Email is { } email) claims.Add(new Claim(ClaimTypes.Email, email));
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role)), Scheme));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> LiveSessions = new();
    internal static readonly TimeSpan SessionFreshFor = TimeSpan.FromSeconds(10);

    /// <summary>Whether session <paramref name="sid"/> of <paramref name="uid"/> is live, looked up at most every few seconds.</summary>
    private static async Task<bool> SessionLiveAsync(ConsoleAccounts accounts, string uid, string sid, CancellationToken ct)
    {
        var key = uid + "/" + sid;
        var now = DateTimeOffset.UtcNow;
        if (LiveSessions.TryGetValue(key, out var seen) && now - seen < SessionFreshFor) return true;
        var live = (await accounts.UseAsync(a => a.ListSessionsAsync(uid, ct))).Any(x => x.Id.ToString("N") == sid);
        if (live) LiveSessions[key] = now; else LiveSessions.TryRemove(key, out _);
        if (LiveSessions.Count > 10_000) foreach (var old in LiveSessions.Where(e => now - e.Value > SessionFreshFor).Select(e => e.Key).ToList()) LiveSessions.TryRemove(old, out _);
        return live;
    }

    private static int Rank(string role) => role switch { ConsoleRoles.Admin => 2, ConsoleRoles.Member => 1, _ => 0 };
    internal static string Lower(string a, string b) => Rank(a) <= Rank(b) ? a : b;

    /// <summary>The role claim the policies expect.</summary>
    private static string RoleName(string role, ConsoleOptions o) => role;
}
