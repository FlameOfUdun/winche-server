using Npgsql;

namespace Winche.Server;

/// <summary>
/// Each project's configuration lives in its own schema of the server's control database (<c>project_&lt;key&gt;</c>), apart
/// from its data. Before a project starts, its schema is made.
/// </summary>
internal static class ConfigSchemas
{
    public static async Task PrepareAsync(NpgsqlDataSource control, ServerProject project, CancellationToken ct)
    {
        var schema = new NpgsqlConnectionStringBuilder(project.ConfigConnectionString).SearchPath!;
        await using var conn = await control.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // One server at a time (several may start together).
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(hashtext('winche-config-schemas'))", ct);
        await Exec(conn, tx, $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", ct);
        await tx.CommitAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
