using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;

namespace Winche.Server;

/// <summary>Where a project's data database is: a database on the control database's server (no host), or on another.</summary>
public sealed record DataLocation(string Database, string? Host = null, int? Port = null, string? Username = null, string? SslMode = null,
    int? PoolMax = null, int? CommandTimeout = null);

/// <summary>Where the data was before a move, kept until the move is finished.</summary>
public sealed record PreviousData(DataLocation Location, string[] Tables, DateTimeOffset MovedAt);

/// <summary>Where data should go: a database (on this server's PostgreSQL without a host, else on another).</summary>
public sealed record DataTarget(string Database, string? Host = null, int? Port = null, string? Username = null, string? Password = null, string? SslMode = null);

/// <summary>What a target looks like before moving there.</summary>
public sealed record DataTargetCheck(bool Ok, string? Problem, string? Version, bool Exists, long RowsToCopy);

public sealed partial class ProjectHosts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, MoveProgress> _moving = new();
    private readonly ConcurrentDictionary<string, (string Error, DateTimeOffset At)> _moveFailed = new();

    internal static string KeyOf(ServerProject project) => project.IsDefault ? DefaultKey : project.Id;

    /// <summary>The move under way for <paramref name="host"/>'s project (it's paused), or null.</summary>
    internal MoveProgress? MovingOf(ProjectHost host) => _moving.TryGetValue(KeyOf(host.Project), out var m) ? m : null;

    /// <summary>The first project's data location (null: in the control database) and where it was before a move.</summary>
    internal async Task<(DataLocation? At, PreviousData? Previous)> DefaultLocationAsync(CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand($"SELECT database, db_host, db_port, db_username, db_ssl_mode, pool_max, command_timeout, previous::text FROM {DefaultTable} WHERE id = $1");
        cmd.Parameters.AddWithValue(DefaultKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        var at = new DataLocation(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6));
        return (at, r.IsDBNull(7) ? null : JsonSerializer.Deserialize<PreviousData>(r.GetString(7), Json));
    }

    /// <summary>A project's data location (the first project in the control database: its name, no host) and previous.</summary>
    internal async Task<(DataLocation At, PreviousData? Previous)> LocationAsync(string key, CancellationToken ct)
    {
        if (key == DefaultKey)
        {
            var (at, previous) = await DefaultLocationAsync(ct);
            return (at ?? throw new InvalidOperationException("Set up the project first: it gets its database then."), previous);
        }
        var row = await RowAsync(key, ct);
        return (row.Location, row.Previous is null ? null : JsonSerializer.Deserialize<PreviousData>(row.Previous, Json));
    }

    private async Task SaveLocationAsync(string key, DataLocation at, PreviousData? previous, CancellationToken ct)
    {
        var prev = previous is null ? (object)DBNull.Value : JsonSerializer.Serialize(previous, Json);
        await using var cmd = key == DefaultKey
            ? Db.CreateCommand($"""
                INSERT INTO {DefaultTable} (id, database, db_host, db_port, db_username, db_ssl_mode, pool_max, command_timeout, previous)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb)
                ON CONFLICT (id) DO UPDATE SET database = $2, db_host = $3, db_port = $4, db_username = $5, db_ssl_mode = $6,
                    pool_max = $7, command_timeout = $8, previous = $9::jsonb
                """)
            : Db.CreateCommand($"""
                UPDATE {Table} SET database = $2, db_host = $3, db_port = $4, db_username = $5, db_ssl_mode = $6, pool_max = $7,
                    command_timeout = $8, previous = $9::jsonb WHERE id = $1
                """);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(at.Database);
        cmd.Parameters.AddWithValue((object?)at.Host ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)at.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)at.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)at.SslMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)at.PoolMax ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)at.CommandTimeout ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter { Value = prev, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<ServerProject> ProjectForKeyAsync(string key, CancellationToken ct) =>
        key == DefaultKey ? await DefaultProjectAsync(ct) : await ProjectForAsync(await RowAsync(key, ct), ct);

    private ProjectHost? HostFor(string key) => key == DefaultKey ? _default : _hosts.TryGetValue(key, out var h) ? h : null;

    private void SetHost(string key, ProjectHost host)
    {
        if (key == DefaultKey) _default = host; else _hosts[key] = host;
    }

    /// <summary>Restarts one project with its location as saved (new app first, the old one stopped a moment later).</summary>
    private async Task RestartAsync(string key, CancellationToken ct)
    {
        var old = HostFor(key);
        SetHost(key, await StartAsync(await ProjectForKeyAsync(key, ct), ct));
        if (old is not null) _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(2)); await StopAsync(old, CancellationToken.None); });
    }

    private Task<string> TargetConnectionAsync(DataTarget t, DataLocation at, CancellationToken ct) =>
        ConnectionForAsync(at, "", ct, t.Password ?? "");

    private static DataLocation LocationOf(DataTarget t, DataLocation current) =>
        new(t.Database.Trim(), string.IsNullOrWhiteSpace(t.Host) ? null : t.Host.Trim(), string.IsNullOrWhiteSpace(t.Host) ? null : t.Port ?? 5432,
            string.IsNullOrWhiteSpace(t.Host) ? null : t.Username, string.IsNullOrWhiteSpace(t.Host) ? null : t.SslMode ?? "Prefer",
            current.PoolMax, current.CommandTimeout);

    /// <summary>Whether <paramref name="t"/> can take the project's data: reachable, empty or creatable, and not where it is now.</summary>
    internal async Task<DataTargetCheck> CheckTargetAsync(string key, DataTarget t, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(t.Database) || t.Database.Length > 63 || t.Database.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            return new(false, "Name the database: letters, digits and underscores, up to 63.", null, false, 0);
        var (current, previous) = await LocationAsync(key, ct);
        if (previous is not null) return new(false, "Finish the last move first.", null, false, 0);
        var target = LocationOf(t, current);
        if (target.Database == current.Database && target.Host == current.Host && (target.Host is null || target.Port == current.Port))
            return new(false, "That's where the data is now.", null, false, 0);
        if (target.Host is null && target.Database == ControlDatabase) return new(false, "That's the control database.", null, false, 0);

        var probe = await ProbeAsync(target, t.Password, ct);
        return probe.Ok ? probe with { RowsToCopy = await RowsToCopyAsync(key, ct) } : probe;
    }

    /// <summary>
    /// Whether <paramref name="target"/> can hold a project's data: reachable, and its database new (and this user may
    /// create it) or empty. Its PostgreSQL version too.
    /// </summary>
    internal async Task<DataTargetCheck> ProbeAsync(DataLocation target, string? password, CancellationToken ct)
    {
        var conn = await ConnectionForAsync(target, "", ct, password ?? "");
        string? version = null;
        bool exists, empty = true, canCreate = false;
        try
        {
            await using var maintenance = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(conn) { Database = "postgres", Pooling = false, Timeout = 10 }.ConnectionString);
            await maintenance.OpenAsync(ct);
            version = maintenance.PostgreSqlVersion.ToString();
            await using (var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)", maintenance) { Parameters = { new() { Value = target.Database } } })
                exists = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            await using (var cmd = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", maintenance))
                canCreate = (bool?)(await cmd.ExecuteScalarAsync(ct)) ?? false;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            // Managed servers may not let us into "postgres": the database itself, then.
            try
            {
                await using var direct = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(conn) { Pooling = false, Timeout = 10 }.ConnectionString);
                await direct.OpenAsync(ct);
                version = direct.PostgreSqlVersion.ToString();
                exists = true;
            }
            catch (Exception inner) when (inner is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            {
                return new(false, $"Couldn't connect: {inner.GetBaseException().Message}", null, false, 0);
            }
        }
        if (exists)
        {
            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(conn) { Pooling = false, Timeout = 10 }.ConnectionString);
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", db);
            empty = (long)(await cmd.ExecuteScalarAsync(ct))! == 0;
        }
        if (exists && !empty) return new(false, $"{target.Database} already has tables: choose an empty or new database.", version, true, 0);
        if (!exists && !canCreate) return new(false, $"{target.Database} doesn't exist and this user can't create databases: create it first.", version, false, 0);
        return new(true, null, version, exists, 0);
    }

    private async Task<long> RowsToCopyAsync(string key, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(await ProjectForKeyAsync(key, ct).ContinueWith(t => t.Result.ConnectionString, ct));
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT relname, n_live_tup FROM pg_stat_user_tables WHERE schemaname = 'public'", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        long rows = 0;
        while (await r.ReadAsync(ct)) rows += r.GetInt64(1);
        return rows;
    }

    /// <summary>
    /// Moves a project's data to <paramref name="t"/>, in the background: the products started here are set up there,
    /// the project pauses (its apps are told to try again), every table is copied and counted, and the project carries on
    /// there. The old data stays until <see cref="FinishMoveAsync"/>. On any failure the project carries on where it was.
    /// </summary>
    internal async Task<MoveProgress> StartMoveAsync(string key, DataTarget t, string? by, CancellationToken ct)
    {
        var check = await CheckTargetAsync(key, t, ct);
        if (!check.Ok) throw new ArgumentException(check.Problem);
        if (_moving.ContainsKey(key)) throw new ArgumentException("A move is already under way.");
        var progress = new MoveProgress();
        _moving[key] = progress;
        _moveFailed.TryRemove(key, out _);
        _ = Task.Run(async () =>
        {
            // The request that asked for it gets its answer before the project pauses.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await MoveAsync(key, t, check.Exists, progress, by);
        });
        return progress;
    }

    private async Task MoveAsync(string key, DataTarget t, bool existed, MoveProgress progress, string? by)
    {
        var ct = CancellationToken.None;
        var (current, _) = await LocationAsync(key, ct);
        var target = LocationOf(t, current);
        var targetConn = await TargetConnectionAsync(t, target, ct);
        var created = false;
        var paused = false;
        await _creating.WaitAsync(ct);
        try
        {
            if (!existed)
            {
                await using var maintenance = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(targetConn) { Database = "postgres", Pooling = false }.ConnectionString);
                await using var create = maintenance.CreateCommand($"CREATE DATABASE \"{target.Database}\"");
                await create.ExecuteNonQueryAsync(ct);
                created = true;
            }

            // The products started here set themselves up there (the project, briefly, on the new database).
            progress.Stage = "preparing";
            var trial = (await ProjectForKeyAsync(key, ct)) with { ConnectionString = targetConn };
            var (app, _) = await ProjectApp.BuildAsync(server, settings, trial, this, configuration, environment, ct);
            await app.StopAsync(ct);
            await app.DisposeAsync();

            // Paused: requests are told to try again; the project's app stops, so nothing writes.
            progress.Stage = "paused";
            paused = true;
            if (HostFor(key) is { } running) await StopAsync(running, ct);
            var sourceConn = (await ProjectForKeyAsync(key, ct)).ConnectionString;
            var tables = await DataCopy.CopyAsync(sourceConn, targetConn, progress, ct);

            // Switch: the new location (its password a secret), the old one kept until the move is finished.
            progress.Stage = "switching";
            var secret = PasswordSecret(key);
            if (await settings.Secrets.GetAsync(secret, ct) is { Length: > 0 } oldPassword) await settings.Secrets.SetAsync(secret + ".previous", oldPassword, by, ct);
            if (target.Host is not null && !string.IsNullOrEmpty(t.Password)) await settings.Secrets.SetAsync(secret, t.Password, by, ct);
            else await settings.Secrets.DeleteAsync(secret, ct);
            await SaveLocationAsync(key, target, new PreviousData(current, [.. tables], DateTimeOffset.UtcNow), ct);
            SetHost(key, await StartAsync(await ProjectForKeyAsync(key, ct), ct));
            logger.LogWarning("Moved project {Project}'s data to {Database} ({Rows} rows)", key, target.Database, progress.Rows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving project {Project}'s data failed", key);
            _moveFailed[key] = (ex is InvalidOperationException ? ex.Message : $"The move failed: {ex.GetBaseException().Message}", DateTimeOffset.UtcNow);
            try
            {
                // Back where it was.
                await SaveLocationAsync(key, current, null, ct);
                if (await settings.Secrets.GetAsync(PasswordSecret(key) + ".previous", ct) is { Length: > 0 } back)
                {
                    await settings.Secrets.SetAsync(PasswordSecret(key), back, by, ct);
                    await settings.Secrets.DeleteAsync(PasswordSecret(key) + ".previous", ct);
                }
                if (paused) SetHost(key, await StartAsync(await ProjectForKeyAsync(key, ct), ct));
                if (created)
                {
                    await using var maintenance = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(targetConn) { Database = "postgres", Pooling = false }.ConnectionString);
                    await using var drop = maintenance.CreateCommand($"DROP DATABASE IF EXISTS \"{target.Database}\" WITH (FORCE)");
                    await drop.ExecuteNonQueryAsync(ct);
                }
            }
            catch (Exception again) { logger.LogError(again, "Couldn't put project {Project} back after the failed move", key); }
        }
        finally
        {
            _moving.TryRemove(key, out _);
            _creating.Release();
        }
    }

    /// <summary>Ends a move: the old data is deleted (or, with <paramref name="deleteOld"/> false, left for you, no longer known here).</summary>
    internal async Task FinishMoveAsync(string key, bool deleteOld, CancellationToken ct)
    {
        var (current, previous) = await LocationAsync(key, ct);
        if (previous is null) return;
        if (deleteOld)
        {
            {
                var conn = await ConnectionForAsync(previous.Location, PasswordSecret(key) + ".previous", ct);
                await using var maintenance = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(conn) { Database = "postgres", Pooling = false }.ConnectionString);
                await using var drop = maintenance.CreateCommand($"DROP DATABASE IF EXISTS \"{previous.Location.Database}\" WITH (FORCE)");
                await drop.ExecuteNonQueryAsync(ct);
            }
        }
        await SaveLocationAsync(key, current, null, ct);
        await settings.Secrets.DeleteAsync(PasswordSecret(key) + ".previous", ct);
    }

    /// <summary>The connection pool for a project's data: most connections at once, and how long a command may run. Restarts it.</summary>
    internal async Task SetPoolAsync(string key, int? max, int? commandTimeout, CancellationToken ct)
    {
        if (max is < 2 or > 500) throw new ArgumentException("Connections: 2 to 500.");
        if (commandTimeout is < 1 or > 3600) throw new ArgumentException("Command timeout: 1 to 3600 seconds.");
        await _creating.WaitAsync(ct);
        try
        {
            var (current, previous) = await LocationAsync(key, ct);
            await SaveLocationAsync(key, current with { PoolMax = max, CommandTimeout = commandTimeout }, previous, ct);
            await RestartAsync(key, ct);
        }
        finally { _creating.Release(); }
    }

    /// <summary>Where a project's data is, for its console.</summary>
    internal async Task<Winche.Console.Project.DataLocationInfo> InfoAsync(string key, CancellationToken ct)
    {
        var (at, previous) = await LocationAsync(key, ct);
        long? size = null;
        string? version = null;
        try
        {
            await using var c = new NpgsqlConnection(await ProjectForKeyAsync(key, ct).ContinueWith(t => t.Result.ConnectionString, ct));
            await c.OpenAsync(ct);
            version = c.PostgreSqlVersion.ToString();
            await using var cmd = new NpgsqlCommand("SELECT pg_database_size(current_database())", c);
            size = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException) { }
        var failed = _moveFailed.TryGetValue(key, out var f) ? new Winche.Console.Project.DataMoveFailure(f.Error, f.At) : null;
        return new(at.Host, at.Host is null ? null : at.Port ?? 5432, at.Database, at.Username, at.SslMode, size, version, at.PoolMax, at.CommandTimeout,
            previous is null ? null : new Winche.Console.Project.DataPrevious(previous.Location.Host, previous.Location.Database, previous.MovedAt),
            failed, DatabaseName(key == DefaultKey ? DefaultKey : key));
    }
}

/// <summary>What a project's console sees of where its data is (<see cref="Winche.Console.Project.IWincheDataLocation"/>).</summary>
internal sealed class ServerDataLocationForConsole(ProjectHosts hosts, string key) : Winche.Console.Project.IWincheDataLocation
{
    private static DataTarget Target(Winche.Console.Project.DataMoveTarget t) => new(t.Database ?? "", t.Host, t.Port, t.Username, t.Password, t.SslMode);

    public Task<Winche.Console.Project.DataLocationInfo> GetAsync(CancellationToken ct) => hosts.InfoAsync(key, ct);

    public async Task<Winche.Console.Project.DataMoveCheck> CheckAsync(Winche.Console.Project.DataMoveTarget target, CancellationToken ct)
    {
        var c = await hosts.CheckTargetAsync(key, Target(target), ct);
        return new(c.Ok, c.Problem, c.Version, c.Exists, c.RowsToCopy);
    }

    public async Task StartMoveAsync(Winche.Console.Project.DataMoveTarget target, string? by, CancellationToken ct) =>
        await hosts.StartMoveAsync(key, Target(target), by, ct);

    public Task FinishMoveAsync(bool deleteOld, CancellationToken ct) => hosts.FinishMoveAsync(key, deleteOld, ct);

    public Task SetPoolAsync(int? maxConnections, int? commandTimeoutSeconds, CancellationToken ct) =>
        hosts.SetPoolAsync(key, maxConnections, commandTimeoutSeconds, ct);
}
