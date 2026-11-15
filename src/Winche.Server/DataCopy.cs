using Npgsql;

namespace Winche.Server;

/// <summary>How far a move has got, for the console (the router answers it while the project is paused).</summary>
public sealed class MoveProgress
{
    public string Stage { get; set; } = "preparing";
    public string? Table { get; set; }
    public int TablesDone { get; set; }
    public int TablesTotal { get; set; }
    public long Rows { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Copies a project's data from one database to another whose tables the products have just made: every table with
/// COPY (in an order its foreign keys allow, its own triggers off), the sequences' positions, then every table's rows
/// counted on both sides. All in one transaction on the new database: it has everything or nothing.
/// </summary>
internal static class DataCopy
{
    /// <summary>Bookkeeping each database keeps for itself (the new one has its own).</summary>
    private static readonly HashSet<string> Bookkeeping = ["__EFMigrationsHistory", "__winche_auth_migrations", "winche_schema"];

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Copies everything; returns the tables copied. Throws (nothing kept on the new side) if anything's off.</summary>
    public static async Task<IReadOnlyList<string>> CopyAsync(string source, string target, MoveProgress progress, CancellationToken ct)
    {
        await using var src = new NpgsqlConnection(source);
        await using var dst = new NpgsqlConnection(target);
        await src.OpenAsync(ct);
        await dst.OpenAsync(ct);
        await using var read = await src.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var write = await dst.BeginTransactionAsync(ct);

        var sourceTables = await TablesAsync(src, ct);
        var targetTables = await TablesAsync(dst, ct);
        var wanted = sourceTables.Where(t => !Bookkeeping.Contains(t)).ToList();
        // Tables the new database didn't get belong to products that aren't started: fine while they're empty.
        foreach (var t in wanted.Except(targetTables).ToList())
        {
            if (await CountAsync(src, t, ct) > 0)
                throw new InvalidOperationException($"{t} has rows, but its product isn't started here, so the new database didn't get it. Start the product, or empty it, first. Nothing was moved.");
            wanted.Remove(t);
        }
        var tables = await OrderAsync(dst, wanted, ct);
        progress.TablesTotal = tables.Count;

        await ExecAsync(dst, $"TRUNCATE {string.Join(", ", tables.Select(t => "public." + Q(t)))} RESTART IDENTITY CASCADE", ct);
        foreach (var t in tables) await ExecAsync(dst, $"ALTER TABLE public.{Q(t)} DISABLE TRIGGER USER", ct);

        progress.Stage = "copying";
        foreach (var t in tables)
        {
            progress.Table = t;
            var sourceColumns = await ColumnsAsync(src, t, ct);
            var columns = string.Join(", ", (await ColumnsAsync(dst, t, ct)).Where(sourceColumns.Contains).Select(Q));
            using (var reader = await src.BeginTextExportAsync($"COPY (SELECT {columns} FROM public.{Q(t)}) TO STDOUT", ct))
            await using (var writer = await dst.BeginTextImportAsync($"COPY public.{Q(t)} ({columns}) FROM STDIN", ct))
            {
                var buffer = new char[64 * 1024];
                int n;
                while ((n = await reader.ReadAsync(buffer, ct)) > 0) await writer.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            progress.TablesDone++;
        }
        foreach (var t in tables) await ExecAsync(dst, $"ALTER TABLE public.{Q(t)} ENABLE TRIGGER USER", ct);

        // Sequences carry on where they were (they can be ahead of the rows).
        await using (var seqs = new NpgsqlCommand("SELECT sequencename, last_value FROM pg_sequences WHERE schemaname = 'public' AND last_value IS NOT NULL", src, read))
        await using (var r = await seqs.ExecuteReaderAsync(ct))
        {
            var positions = new List<(string Name, long Value)>();
            while (await r.ReadAsync(ct)) positions.Add((r.GetString(0), r.GetInt64(1)));
            await r.CloseAsync();
            var targetSeqs = await ListAsync(dst, "SELECT sequencename FROM pg_sequences WHERE schemaname = 'public'", ct);
            foreach (var (name, value) in positions.Where(p => targetSeqs.Contains(p.Name)))
                await ExecAsync(dst, $"SELECT setval('public.{Q(name).Replace("'", "''")}', {value}, true)", ct);
        }

        // Every table's rows, counted on both sides.
        progress.Stage = "checking";
        foreach (var t in tables)
        {
            var before = await CountAsync(src, t, ct);
            var after = await CountAsync(dst, t, ct);
            if (before != after) throw new InvalidOperationException($"{t} has {before} rows here but {after} in the new database. Nothing was moved.");
            progress.Rows += after;
        }
        await write.CommitAsync(ct);
        return tables;
    }

    /// <summary>The project's tables in public.</summary>
    internal static Task<List<string>> TablesAsync(NpgsqlConnection c, CancellationToken ct) =>
        ListAsync(c, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", ct);

    /// <summary>Columns that can be written (not generated), in order.</summary>
    private static Task<List<string>> ColumnsAsync(NpgsqlConnection c, string table, CancellationToken ct) => ListAsync(c, $"""
        SELECT a.attname FROM pg_attribute a
        WHERE a.attrelid = 'public.{Q(table).Replace("'", "''")}'::regclass AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
        ORDER BY a.attnum
        """, ct);

    /// <summary>Tables in an order their foreign keys allow (referenced first).</summary>
    private static async Task<List<string>> OrderAsync(NpgsqlConnection c, List<string> tables, CancellationToken ct)
    {
        var edges = new List<(string From, string To)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.conrelid::regclass::text, c.confrelid::regclass::text FROM pg_constraint c
            WHERE c.contype = 'f' AND c.connamespace = 'public'::regnamespace
            """, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) edges.Add((r.GetString(0).Trim('"'), r.GetString(1).Trim('"')));
        var ordered = new List<string>();
        var left = new HashSet<string>(tables);
        while (left.Count > 0)
        {
            var ready = left.Where(t => !edges.Any(e => e.From == t && e.To != t && left.Contains(e.To))).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (ready.Count == 0) ready = [.. left.OrderBy(t => t, StringComparer.Ordinal)];   // a cycle: as they come
            ordered.AddRange(ready);
            left.ExceptWith(ready);
        }
        return ordered;
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM public.{Q(table)}", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>What the first project logged into the server's database before setup gave it one of its own: dropped.</summary>
    internal static async Task DropPreSetupAsync(NpgsqlDataSource control, CancellationToken ct)
    {
        await using var c = await control.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ExecAsync(c, "DROP TABLE IF EXISTS public.console_logs, public.console_log_queries, public.console_log_settings CASCADE", ct);
        await ExecAsync(c, "DELETE FROM public.winche_schema WHERE component = 'console-logs'", ct);
        await tx.CommitAsync(ct);
    }
}
