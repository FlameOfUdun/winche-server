using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Winche.Console.Identity;

/// <summary>
/// A project's service account: a named caller for another server or CI, with a role (viewer, member, admin) or deploy
/// scopes (<see cref="ConsoleCiScope"/>). It belongs to the project, not to whoever made it.
/// </summary>
public sealed record ServiceAccount(string Id, string Name, string? Role, IReadOnlyList<string> Scopes, bool Disabled,
    string CreatedBy, string? CreatedByEmail, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// A key of a service account (<c>wsk_&lt;id&gt;_&lt;secret&gt;</c>, shown once, only its hash kept), exchanged for
/// access tokens. At most two live at once, so one is replaced with no downtime.
/// </summary>
public sealed record ServiceAccountKey(string Id, string AccountId, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt, string? LastUsedFrom);

/// <summary>
/// An identity a service account trusts instead of a key: tokens signed by <see cref="Issuer"/> (OpenID Connect) whose
/// subject matches <see cref="Subject"/> (<c>*</c> matches any run of characters) and, if set, whose audience is
/// <see cref="Audience"/>.
/// </summary>
public sealed record ServiceAccountIdentity(string Id, string AccountId, string Issuer, string Subject, string? Audience,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// The project's service accounts, their keys and trusted identities, in the project's config database
/// (<c>AddWincheConfig</c>).
/// </summary>
public sealed class ServiceAccountStore
{
    public const string KeyPrefix = "wsk_";
    /// <summary>The most live keys an account may have at once.</summary>
    public const int MaxKeys = 2;
    internal const string Accounts = "winche_service_accounts", Keys = "winche_service_account_keys", Identities = "winche_service_account_identities";

    private static readonly string[] Schema =
    [
        $"""
        CREATE TABLE IF NOT EXISTS {Accounts} (
            id               text        PRIMARY KEY,
            name             text        NOT NULL UNIQUE,
            role             text        NULL,
            scopes           text[]      NOT NULL,
            disabled_at      timestamptz NULL,
            created_by       text        NOT NULL,
            created_by_email text        NULL,
            created_at       timestamptz NOT NULL,
            last_used_at     timestamptz NULL
        );
        CREATE TABLE IF NOT EXISTS {Keys} (
            id             text        PRIMARY KEY,
            account_id     text        NOT NULL REFERENCES {Accounts}(id) ON DELETE CASCADE,
            hash           bytea       NOT NULL,
            created_by     text        NOT NULL,
            created_at     timestamptz NOT NULL,
            expires_at     timestamptz NOT NULL,
            last_used_at   timestamptz NULL,
            last_used_from text        NULL,
            revoked_at     timestamptz NULL
        );
        CREATE TABLE IF NOT EXISTS {Identities} (
            id           text        PRIMARY KEY,
            account_id   text        NOT NULL REFERENCES {Accounts}(id) ON DELETE CASCADE,
            issuer       text        NOT NULL,
            subject      text        NOT NULL,
            audience     text        NULL,
            created_by   text        NOT NULL,
            created_at   timestamptz NOT NULL,
            last_used_at timestamptz NULL
        );
        """,
    ];

    /// <summary>
    /// CI tokens (from before service accounts) become accounts with deploy scopes, each with its token as its key: a
    /// pipeline's WINCHE_TOKEN keeps working (the CLI exchanges it), and the tokens themselves are no longer accepted.
    /// Run when the store starts (no new CI tokens are made, so again is harmless).
    /// </summary>
    private static readonly string MoveCiTokens = $"""
        DO $$
        BEGIN
          IF to_regclass('{ConsoleTokenStore.Tokens}') IS NOT NULL THEN
            INSERT INTO {Accounts} (id, name, role, scopes, created_by, created_by_email, created_at, last_used_at)
              SELECT t.id, t.name, NULL, t.scopes, t.user_id, t.user_email, t.created_at, t.last_used_at
              FROM {ConsoleTokenStore.Tokens} t WHERE t.kind = 'ci' AND t.revoked_at IS NULL AND t.expires_at > now()
              ON CONFLICT (name) DO NOTHING;
            INSERT INTO {Accounts} (id, name, role, scopes, created_by, created_by_email, created_at, last_used_at)
              SELECT t.id, t.name || '-' || left(t.id, 6), NULL, t.scopes, t.user_id, t.user_email, t.created_at, t.last_used_at
              FROM {ConsoleTokenStore.Tokens} t WHERE t.kind = 'ci' AND t.revoked_at IS NULL AND t.expires_at > now()
                AND NOT EXISTS (SELECT 1 FROM {Accounts} a WHERE a.id = t.id)
              ON CONFLICT DO NOTHING;
            INSERT INTO {Keys} (id, account_id, hash, created_by, created_at, expires_at, last_used_at)
              SELECT t.id, t.id, t.hash, coalesce(t.user_email, t.user_id), t.created_at, t.expires_at, t.last_used_at
              FROM {ConsoleTokenStore.Tokens} t WHERE t.kind = 'ci' AND t.revoked_at IS NULL AND t.expires_at > now()
                AND EXISTS (SELECT 1 FROM {Accounts} a WHERE a.id = t.id)
              ON CONFLICT (id) DO NOTHING;
            UPDATE {ConsoleTokenStore.Tokens} SET revoked_at = now() WHERE kind = 'ci' AND revoked_at IS NULL;
          END IF;
        END $$;
        """;

    private readonly NpgsqlDataSource? _db;
    private readonly TimeProvider _time;
    private bool _ready;

    public ServiceAccountStore(IServiceProvider services, TimeProvider? time = null)
    {
        _db = services.GetKeyedService<NpgsqlDataSource>("WincheConfigOptions");
        _time = time ?? TimeProvider.System;
    }

    /// <summary>False without <c>AddWincheConfig</c>: there's nowhere to keep them.</summary>
    public bool Available => _db is not null;

    private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Service accounts need AddWincheConfig(…).");

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ready) return;
        await Winche.Core.WincheSchema.EnsureAsync(Db, "service-accounts", Schema, ct);
        await using (var move = Db.CreateCommand(MoveCiTokens)) await move.ExecuteNonQueryAsync(ct);
        _ready = true;
    }

    internal static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static string Random(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private NpgsqlCommand Command(string sql, params object?[] values)
    {
        var cmd = Db.CreateCommand(sql);
        foreach (var v in values) cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        return cmd;
    }

    private const string AccountColumns = "id, name, role, scopes, disabled_at IS NOT NULL, created_by, created_by_email, created_at, last_used_at";

    private static ServiceAccount ReadAccount(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetFieldValue<string[]>(3), r.GetBoolean(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));

    // ── accounts ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceAccount>> ListAsync(CancellationToken ct = default)
    {
        if (_db is null) return [];
        await EnsureAsync(ct);
        await using var cmd = Command($"SELECT {AccountColumns} FROM {Accounts} ORDER BY name");
        var list = new List<ServiceAccount>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadAccount(r));
        return list;
    }

    public async Task<ServiceAccount?> GetAsync(string id, CancellationToken ct = default)
    {
        if (_db is null) return null;
        await EnsureAsync(ct);
        await using var cmd = Command($"SELECT {AccountColumns} FROM {Accounts} WHERE id = $1", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAccount(r) : null;
    }

    /// <summary>A new account; null when the name is taken.</summary>
    public async Task<ServiceAccount?> CreateAsync(string name, string? role, IReadOnlyList<string> scopes, string by, string? byEmail, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var id = Random(8);
        var now = _time.GetUtcNow();
        await using var cmd = Command($"""
            INSERT INTO {Accounts} (id, name, role, scopes, created_by, created_by_email, created_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (name) DO NOTHING
            """, id, name, role, scopes.ToArray(), by, byEmail, now);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? new ServiceAccount(id, name, role, scopes, false, by, byEmail, now, null) : null;
    }

    /// <summary>Changes the account's role or scopes, or turns it off or on; false when there's no such account.</summary>
    public async Task<bool> UpdateAsync(string id, string? role, IReadOnlyList<string> scopes, bool disabled, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"""
            UPDATE {Accounts} SET role = $2, scopes = $3,
              disabled_at = CASE WHEN $4 THEN coalesce(disabled_at, now()) ELSE NULL END
            WHERE id = $1
            """, id, role, scopes.ToArray(), disabled);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Deletes the account with its keys and identities.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"DELETE FROM {Accounts} WHERE id = $1", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ── keys ──────────────────────────────────────────────────────────────────────────────────────────────────

    private const string KeyColumns = "id, account_id, created_by, created_at, expires_at, last_used_at, last_used_from";

    private static ServiceAccountKey ReadKey(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetString(6));

    /// <summary>The account's live keys, newest first.</summary>
    public async Task<IReadOnlyList<ServiceAccountKey>> KeysAsync(string accountId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"""
            SELECT {KeyColumns} FROM {Keys} WHERE account_id = $1 AND revoked_at IS NULL AND expires_at > now() ORDER BY created_at DESC
            """, accountId);
        var list = new List<ServiceAccountKey>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadKey(r));
        return list;
    }

    /// <summary>A new key (the secret is shown once); null when the account already has <see cref="MaxKeys"/> live keys.</summary>
    public async Task<(ServiceAccountKey Key, string Secret)?> AddKeyAsync(string accountId, TimeSpan lifetime, string by, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var id = Random(8);
        var secret = Random(24);
        var now = _time.GetUtcNow();
        await using var cmd = Command($"""
            INSERT INTO {Keys} (id, account_id, hash, created_by, created_at, expires_at)
            SELECT $1, $2, $3, $4, $5, $6
            WHERE (SELECT count(*) FROM {Keys} WHERE account_id = $2 AND revoked_at IS NULL AND expires_at > now()) < {MaxKeys}
            """, id, accountId, Hash(secret), by, now, now + lifetime);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        return (new ServiceAccountKey(id, accountId, by, now, now + lifetime, null, null), $"{KeyPrefix}{id}_{secret}");
    }

    /// <summary>Adds an existing secret's hash as a key (CI tokens becoming accounts keep working).</summary>
    internal async Task AddKeyHashAsync(string keyId, string accountId, byte[] hash, string by, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        DateTimeOffset? lastUsedAt, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"""
            INSERT INTO {Keys} (id, account_id, hash, created_by, created_at, expires_at, last_used_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (id) DO NOTHING
            """, keyId, accountId, hash, by, createdAt, expiresAt, lastUsedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RevokeKeyAsync(string accountId, string keyId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"UPDATE {Keys} SET revoked_at = now() WHERE id = $1 AND account_id = $2 AND revoked_at IS NULL", keyId, accountId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// The account and key a key secret (<c>wsk_…</c>, or a CI token's <c>wct_…</c>) belongs to, when both are live;
    /// notes the use (when and from where).
    /// </summary>
    public async Task<(ServiceAccount Account, ServiceAccountKey Key)?> FindByKeyAsync(string secret, string? from, CancellationToken ct = default)
    {
        if (_db is null) return null;
        var prefix = secret.StartsWith(KeyPrefix, StringComparison.Ordinal) ? KeyPrefix : secret.StartsWith(ConsoleTokenStore.Prefix, StringComparison.Ordinal) ? ConsoleTokenStore.Prefix : null;
        if (prefix is null) return null;
        var parts = secret[prefix.Length..].Split('_', 2);
        if (parts is not [{ Length: 16 } id, { Length: 48 } rest]) return null;
        await EnsureAsync(ct);
        ServiceAccountKey key;
        await using (var cmd = Command($"""
            SELECT {KeyColumns}, hash FROM {Keys} WHERE id = $1 AND revoked_at IS NULL AND expires_at > now()
            """, id))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            // A CI token's hash was of its secret part; a key's of its secret part too.
            if (!CryptographicOperations.FixedTimeEquals(r.GetFieldValue<byte[]>(7), Hash(rest))) return null;
            key = ReadKey(r);
        }
        if (await GetAsync(key.AccountId, ct) is not { Disabled: false } account) return null;
        await using (var touch = Command($"UPDATE {Keys} SET last_used_at = now(), last_used_from = $2 WHERE id = $1", id, from))
            await touch.ExecuteNonQueryAsync(ct);
        await TouchAccountAsync(account.Id, ct);
        return (account, key);
    }

    private async Task TouchAccountAsync(string accountId, CancellationToken ct)
    {
        await using var cmd = Command($"UPDATE {Accounts} SET last_used_at = now() WHERE id = $1", accountId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Whether a key (or identity) an access token was made from is still live, its account still on.</summary>
    public async Task<ServiceAccount?> LiveAsync(string accountId, string credential, CancellationToken ct = default)
    {
        if (await GetAsync(accountId, ct) is not { Disabled: false } account) return null;
        await using var cmd = Command($"""
            SELECT 1 FROM {Keys} WHERE id = $1 AND account_id = $2 AND revoked_at IS NULL AND expires_at > now()
            UNION ALL SELECT 1 FROM {Identities} WHERE id = $1 AND account_id = $2
            """, credential, accountId);
        return await cmd.ExecuteScalarAsync(ct) is null ? null : account;
    }

    // ── trusted identities ────────────────────────────────────────────────────────────────────────────────────

    private const string IdentityColumns = "id, account_id, issuer, subject, audience, created_by, created_at, last_used_at";

    private static ServiceAccountIdentity ReadIdentity(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));

    public async Task<IReadOnlyList<ServiceAccountIdentity>> IdentitiesAsync(string? accountId = null, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = accountId is null
            ? Command($"SELECT {IdentityColumns} FROM {Identities} ORDER BY created_at")
            : Command($"SELECT {IdentityColumns} FROM {Identities} WHERE account_id = $1 ORDER BY created_at", accountId);
        var list = new List<ServiceAccountIdentity>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadIdentity(r));
        return list;
    }

    public async Task<ServiceAccountIdentity> AddIdentityAsync(string accountId, string issuer, string subject, string? audience, string by, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var identity = new ServiceAccountIdentity(Random(8), accountId, issuer, subject, audience, by, _time.GetUtcNow(), null);
        await using var cmd = Command($"""
            INSERT INTO {Identities} (id, account_id, issuer, subject, audience, created_by, created_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, identity.Id, accountId, issuer, subject, audience, by, identity.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return identity;
    }

    public async Task<bool> RemoveIdentityAsync(string accountId, string identityId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Command($"DELETE FROM {Identities} WHERE id = $1 AND account_id = $2", identityId, accountId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Notes an identity's use.</summary>
    internal async Task TouchIdentityAsync(ServiceAccountIdentity identity, CancellationToken ct)
    {
        await using (var cmd = Command($"UPDATE {Identities} SET last_used_at = now() WHERE id = $1", identity.Id)) await cmd.ExecuteNonQueryAsync(ct);
        await TouchAccountAsync(identity.AccountId, ct);
    }

    /// <summary>Whether <paramref name="subject"/> matches <paramref name="pattern"/> (<c>*</c>: any run of characters).</summary>
    public static bool Matches(string pattern, string subject)
    {
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(subject, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
}
