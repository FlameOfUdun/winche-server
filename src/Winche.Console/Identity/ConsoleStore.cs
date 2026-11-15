using Npgsql;
using Winche.Console.Options;

namespace Winche.Console.Identity;

/// <summary>
/// The console's own tables beside its accounts (which are a Winche Auth of their own): pending invites, and identity
/// providers' back-channel sign-outs. In the accounts' database (<see cref="ConsoleOptions.AccountsConnectionString"/>,
/// else the project's), made and upgraded with <see cref="Winche.Core.WincheSchema"/>.
/// </summary>
public sealed class ConsoleStore : IAsyncDisposable
{
    public const string Invites = "winche_console_invites", SignOuts = "winche_console_provider_signouts";

    private static readonly string[] Schema =
    [
        // The ASP.NET Identity tables of before (console accounts are a Winche Auth now), and their migrations' rows.
        """
        DROP TABLE IF EXISTS "AspNetUserTokens", "AspNetUserRoles", "AspNetUserLogins", "AspNetUserClaims", "AspNetRoleClaims",
            "AspNetUsers", "AspNetRoles", "Invites", "ConsoleProviderSignOuts" CASCADE;
        DO $$ BEGIN
            IF to_regclass('"__EFMigrationsHistory"') IS NOT NULL THEN
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" IN
                    ('20260621160022_InitialIdentity', '20260629115701_AddConsoleInvites', '20261114224122_AddConsoleProviderSignOuts');
            END IF;
        END $$;
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {Invites} (
            id uuid PRIMARY KEY,
            email text NOT NULL,
            role text NOT NULL,
            first_name text,
            last_name text,
            require_name boolean NOT NULL DEFAULT false,
            require_two_factor boolean NOT NULL DEFAULT false,
            expires_at timestamptz NOT NULL,
            created_at timestamptz NOT NULL,
            created_by text,
            accepted_at timestamptz,
            revoked_at timestamptz
        );
        CREATE INDEX IF NOT EXISTS {Invites}_email ON {Invites} (email);
        CREATE TABLE IF NOT EXISTS {SignOuts} (
            id bigserial PRIMARY KEY,
            provider text NOT NULL,
            sid text,
            subject text,
            at timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS {SignOuts}_sid ON {SignOuts} (provider, sid);
        CREATE INDEX IF NOT EXISTS {SignOuts}_subject ON {SignOuts} (provider, subject);
        """,
    ];

    private readonly NpgsqlDataSource _db;
    private volatile bool _ready;

    public ConsoleStore(ConsoleOptions options) => _db = NpgsqlDataSource.Create(options.AccountsDatabase);

    /// <summary>Makes (or upgrades) the tables; also done before first use.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await Winche.Core.WincheSchema.EnsureAsync(_db, "console", Schema, ct);
        _ready = true;
    }

    // ── invites ─────────────────────────────────────────────────────────────────

    private const string InviteColumns =
        "id, email, role, first_name, last_name, require_name, require_two_factor, expires_at, created_at, created_by, accepted_at, revoked_at";

    /// <summary>Invites not accepted yet, newest first.</summary>
    public async Task<List<ConsoleInvite>> OpenInvitesAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"SELECT {InviteColumns} FROM {Invites} WHERE accepted_at IS NULL ORDER BY created_at DESC");
        return await ReadInvitesAsync(cmd, ct);
    }

    public async Task<ConsoleInvite?> FindInviteAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"SELECT {InviteColumns} FROM {Invites} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await ReadInvitesAsync(cmd, ct)).FirstOrDefault();
    }

    /// <summary>Whether <paramref name="email"/> has an invite still waiting (not accepted, revoked or expired).</summary>
    public async Task<bool> HasPendingInviteAsync(string email, DateTimeOffset now, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand(
            $"SELECT EXISTS (SELECT 1 FROM {Invites} WHERE email = $1 AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > $2)");
        cmd.Parameters.AddWithValue(email);
        cmd.Parameters.AddWithValue(now);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Adds the invite, or saves it as it now is.</summary>
    public async Task SaveInviteAsync(ConsoleInvite i, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"""
            INSERT INTO {Invites} ({InviteColumns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (id) DO UPDATE SET email = $2, role = $3, first_name = $4, last_name = $5, require_name = $6,
                require_two_factor = $7, expires_at = $8, created_at = $9, created_by = $10, accepted_at = $11, revoked_at = $12
            """);
        foreach (var v in new object?[] { i.Id, i.Email, i.Role, i.FirstName, i.LastName, i.RequireName, i.RequireTwoFactor, i.ExpiresAt,
                     i.CreatedAt, i.CreatedByUserId, i.AcceptedAt, i.RevokedAt })
            cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<ConsoleInvite>> ReadInvitesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<ConsoleInvite>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ConsoleInvite
            {
                Id = r.GetGuid(0), Email = r.GetString(1), Role = r.GetString(2),
                FirstName = r.IsDBNull(3) ? null : r.GetString(3), LastName = r.IsDBNull(4) ? null : r.GetString(4),
                RequireName = r.GetBoolean(5), RequireTwoFactor = r.GetBoolean(6),
                ExpiresAt = r.GetFieldValue<DateTimeOffset>(7), CreatedAt = r.GetFieldValue<DateTimeOffset>(8),
                CreatedByUserId = r.IsDBNull(9) ? null : r.GetString(9),
                AcceptedAt = r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
                RevokedAt = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
            });
        return list;
    }

    // ── providers' back-channel sign-outs ───────────────────────────────────────

    /// <summary>Keeps a provider's sign-out (one session, or all of a person's), dropping those older than <paramref name="keep"/>.</summary>
    public async Task AddSignOutAsync(string provider, string? sid, string? subject, TimeSpan keep, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var batch = _db.CreateBatch();
        var add = new NpgsqlBatchCommand($"INSERT INTO {SignOuts} (provider, sid, subject, at) VALUES ($1, $2, $3, $4)");
        foreach (var v in new object?[] { provider, sid, subject, now }) add.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        var prune = new NpgsqlBatchCommand($"DELETE FROM {SignOuts} WHERE at < $1");
        prune.Parameters.Add(new NpgsqlParameter { Value = now - keep });
        batch.BatchCommands.Add(add);
        batch.BatchCommands.Add(prune);
        await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Whether the provider signed out this session (by <paramref name="sid"/>), or all of the person's since <paramref name="since"/>.</summary>
    public async Task<bool> SignedOutAsync(string provider, string? sid, string? subject, DateTimeOffset since, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"""
            SELECT EXISTS (SELECT 1 FROM {SignOuts} WHERE provider = $1
                AND (($2::text IS NOT NULL AND sid = $2) OR (sid IS NULL AND $3::text IS NOT NULL AND subject = $3 AND at > $4)))
            """);
        foreach (var v in new object?[] { provider, sid, subject, since }) cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
