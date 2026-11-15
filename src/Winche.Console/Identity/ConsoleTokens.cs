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

/// <summary>
/// A console API token: <c>cli</c> (made when someone approves <c>winche login</c> in the browser; acts as them, with
/// their role as it is now) or <c>ci</c> (made by an admin for CI; named, limited to <see cref="Scopes"/>, and never
/// more than its maker can do).
/// </summary>
public sealed record ConsoleToken(string Id, string Kind, string Name, string UserId, string? UserEmail, string Role,
    IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>Marks an endpoint a CI token with <see cref="Scope"/> may call (CI tokens can call nothing else).</summary>
public sealed record ConsoleCiScope(string Scope)
{
    public const string Functions = "functions", Rules = "rules", Indexes = "indexes";
    public static readonly string[] All = [Functions, Rules, Indexes];
}

/// <summary>
/// The console's API tokens and the one-time codes of the CLI's browser sign-in, in the project's config database
/// (<c>AddWincheConfig</c>). Tokens are <c>wct_&lt;id&gt;_&lt;secret&gt;</c>; only the secret's hash is kept.
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

    /// <summary>A new token; the returned string is shown once.</summary>
    public async Task<(ConsoleToken Token, string Secret)> CreateAsync(string kind, string name, string userId, string? email, string role,
        IReadOnlyList<string> scopes, TimeSpan lifetime, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var id = Random(8);
        var secret = Random(24);
        var now = _time.GetUtcNow();
        await using var cmd = Db.CreateCommand($"""
            INSERT INTO {Tokens} (id, kind, name, user_id, user_email, role, scopes, hash, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """);
        foreach (var p in new object?[] { id, kind, name, userId, (object?)email ?? DBNull.Value, role, scopes.ToArray(), Hash(secret), now, now + lifetime })
            cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        return (new ConsoleToken(id, kind, name, userId, email, role, scopes, now, now + lifetime, null), $"{Prefix}{id}_{secret}");
    }

    /// <summary>The live token <paramref name="bearer"/> is, or null (wrong, expired, revoked).</summary>
    public async Task<ConsoleToken?> FindAsync(string bearer, CancellationToken ct = default)
    {
        if (_db is null || !bearer.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = bearer[Prefix.Length..].Split('_', 2);
        if (parts is not [{ Length: 16 } id, { Length: 48 } secret]) return null;
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"""
            SELECT id, kind, name, user_id, user_email, role, scopes, created_at, expires_at, last_used_at, hash
            FROM {Tokens} WHERE id = $1 AND revoked_at IS NULL AND expires_at > now()
            """);
        cmd.Parameters.AddWithValue(id);
        ConsoleToken? token = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            if (!CryptographicOperations.FixedTimeEquals(r.GetFieldValue<byte[]>(10), Hash(secret))) return null;
            token = Read(r);
        }
        // Last use, at most once a minute.
        if (token.LastUsedAt is null || _time.GetUtcNow() - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            await using var touch = Db.CreateCommand($"UPDATE {Tokens} SET last_used_at = now() WHERE id = $1");
            touch.Parameters.AddWithValue(id);
            await touch.ExecuteNonQueryAsync(ct);
        }
        return token;
    }

    private static ConsoleToken Read(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetFieldValue<string[]>(6), r.GetFieldValue<DateTimeOffset>(7),
        r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));

    /// <summary>The live tokens, newest first.</summary>
    public async Task<IReadOnlyList<ConsoleToken>> ListAsync(CancellationToken ct = default)
    {
        if (_db is null) return [];
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"""
            SELECT id, kind, name, user_id, user_email, role, scopes, created_at, expires_at, last_used_at
            FROM {Tokens} WHERE revoked_at IS NULL AND expires_at > now() ORDER BY created_at DESC
            """);
        var list = new List<ConsoleToken>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Revokes token <paramref name="id"/>; false when there's no such live token.</summary>
    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"UPDATE {Tokens} SET revoked_at = now() WHERE id = $1 AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

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
/// console's accounts, renewed with its refresh token), which acts as its account with its role here now; or a CI token
/// (<c>wct_…</c>), which acts as itself, with the lower of its role and its maker's, and only on endpoints marked with
/// one of its scopes (<see cref="ConsoleCiScope"/>). Either stops working once its account is turned off or removed.
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
        if (!raw.StartsWith(ConsoleTokenStore.Prefix, StringComparison.Ordinal))
            return raw.Count(c => c == '.') == 2 ? await SessionAsync(raw) : AuthenticateResult.NoResult();
        var store = Context.RequestServices.GetRequiredService<ConsoleTokenStore>();
        if (await store.FindAsync(header[7..].Trim(), Context.RequestAborted) is not { } token)
            return AuthenticateResult.Fail("That token isn't valid (wrong, expired or revoked): run winche login, or make a new CI token.");

        var console = Context.RequestServices.GetRequiredService<ConsoleOptions>();
        var role = token.Role;
        string? email = token.UserEmail;
        {
            var accounts = Context.RequestServices.GetRequiredService<ConsoleAccounts>();
            var user = await accounts.FindAsync(token.UserId, Context.RequestAborted);
            if (user is not { Active: true }) return AuthenticateResult.Fail("The token's user is no longer in the console.");
            // Its role here now: the account's, or (a created project) its membership's.
            var now = await Api.AuthEndpoints.RoleHereAsync(user, console, Context.RequestServices);
            if (now is null) return AuthenticateResult.Fail("The token's user has no console role.");
            role = Lower(token.Role, now);
            email = user.Email;
        }

        // (Tokens are CI tokens: the CLI's sign-in is an account session, above.)
        {
            var allowed = Context.GetEndpoint()?.Metadata.GetOrderedMetadata<ConsoleCiScope>() ?? [];
            if (!allowed.Any(s => token.Scopes.Contains(s.Scope)))
                return AuthenticateResult.Fail($"The CI token '{token.Name}' can only deploy ({string.Join(", ", token.Scopes)}).");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.UserId),
            new(TokenIdClaim, token.Id),
            new(TokenKindClaim, token.Kind),
            new(ClaimTypes.Role, RoleName(role, console)),
            // Its role here is settled (the lower of its own and its maker's in this project): never read as an account's.
            new(Project.ProjectRoleClaims.DoneClaim, role),
        };
        // The version history names the token and who made it: "gitlab-main (CI token by ann@example.com)".
        claims.Add(new Claim(ClaimTypes.Name, $"{token.Name} (CI token by {token.UserEmail ?? token.UserId})"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme));
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
