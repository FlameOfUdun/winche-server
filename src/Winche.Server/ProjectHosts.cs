using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Winche.Core;

namespace Winche.Server;

/// <summary>
/// The server a project's app runs on: no sockets. The router hands it requests that came in on the real server
/// (<see cref="HandleAsync"/>), and the project's app runs them through its own pipeline and services.
/// </summary>
internal sealed class InProcessServer : IServer
{
    private Func<IFeatureCollection, Task>? _handle;

    public IFeatureCollection Features { get; } = new FeatureCollection();

    public InProcessServer() => Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
    {
        _handle = async features =>
        {
            var context = application.CreateContext(features);
            Exception? failure = null;
            try
            {
                await application.ProcessRequestAsync(context);
                // A response with no body (401, 404, 204…) would otherwise start after this context is gone, and the
                // app's OnStarting callbacks (CORS headers) would run against a disposed context: start it here.
                if (features.Get<IHttpResponseFeature>() is { HasStarted: false } && features.Get<IHttpResponseBodyFeature>() is { } body)
                    try { await body.StartAsync(); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }   // the client went away
            }
            catch (Exception ex) { failure = ex; throw; }
            finally { application.DisposeContext(context, failure); }
        };
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _handle = null;
        return Task.CompletedTask;
    }

    public void Dispose() { }

    /// <summary>
    /// Runs <paramref name="front"/>'s request in the project's app, seen at <paramref name="pathBase"/> +
    /// <paramref name="path"/>. The project's app gets its own HttpContext over the same connection.
    /// </summary>
    public Task HandleAsync(HttpContext front, PathString pathBase, PathString path)
    {
        var handle = _handle ?? throw new InvalidOperationException("This project isn't running.");
        var request = front.Features.GetRequiredFeature<IHttpRequestFeature>();
        var features = new FeatureCollection(front.Features);
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Protocol = request.Protocol, Scheme = request.Scheme, Method = request.Method,
            PathBase = pathBase, Path = path, QueryString = request.QueryString, RawTarget = request.RawTarget,
            Headers = request.Headers, Body = request.Body,
        });
        return handle(features);
    }
}

/// <summary>A running project: its app, and the server the router hands its requests to.</summary>
internal sealed record ProjectHost(ServerProject Project, WebApplication App, InProcessServer Server)
{
    /// <summary>The project's id as its console has it now (the default project's comes from its setup).</summary>
    public string CurrentId => Project.IsDefault
        ? App.Services.GetRequiredService<Winche.Console.Project.ProjectSetup>().Current.Id
        : Project.Id;
}

/// <summary>
/// The projects this server runs. The default one is the server's own database (<c>Winche__ConnectionString</c>); the
/// others are listed in its table <c>winche_server_projects</c>, each with a database of its own, made when the
/// project is created (the server's database user needs CREATEDB), and files under <c>ProjectsPath/&lt;id&gt;</c>.
/// </summary>
public sealed partial class ProjectHosts(ServerOptions server, ServerSettingsProvider settings, IConfiguration configuration,
    IWebHostEnvironment environment, ILogger<ProjectHosts> logger, ConsoleAccounts accounts) : IHostedService
{
    private const string Table = "winche_server_projects";

    /// <summary>The console's accounts, for every project's console.</summary>
    internal ConsoleAccounts Accounts => accounts;
    private const string DefaultTable = "winche_server_default_data";

    /// <summary>The first project's key for its configuration schema (its id can change during setup).</summary>
    internal const string DefaultKey = "default";
    private readonly ConcurrentDictionary<string, ProjectHost> _hosts = new();
    private readonly SemaphoreSlim _creating = new(1, 1);
    private ProjectHost? _default;

    private NpgsqlDataSource? _db;
    private NpgsqlDataSource Db => _db ??= NpgsqlDataSource.Create(server.ConnectionString);

    /// <summary>The control database, shared by every project for what they share (data-protection keys).</summary>
    internal NpgsqlDataSource ControlDb => Db;

    /// <summary>A project in the list; <see cref="Host"/> null: its database is on the control database's server.</summary>
    public sealed record ProjectRow(string Id, string Name, string Database, DateTimeOffset CreatedAt, DateTimeOffset? ArchivedAt = null,
        string? Host = null, int? Port = null, string? Username = null, string? SslMode = null, int? PoolMax = null, int? CommandTimeout = null,
        string? Previous = null)
    {
        public DataLocation Location => new(Database, Host, Port, Username, SslMode, PoolMax, CommandTimeout);
    }

    /// <summary>The secret holding a project's database password, when its database is elsewhere.</summary>
    internal static string PasswordSecret(string id) => $"project.{id}.password";

    private readonly ConcurrentDictionary<string, bool> _archived = new();

    /// <summary>Whether the project at <c>/p/&lt;id&gt;</c> is archived (its address is refused with 410).</summary>
    internal bool IsArchived(string id) => _archived.ContainsKey(id);

    private Task EnsureAsync(CancellationToken ct) => Winche.Core.WincheSchema.EnsureAsync(Db, "server-projects",
    [
        $"""
        CREATE TABLE IF NOT EXISTS {Table} (
            id         text        PRIMARY KEY,
            name       text        NOT NULL,
            database   text        NOT NULL,
            created_at timestamptz NOT NULL
        );
        """,
        $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS archived_at timestamptz NULL;",
        // Where a project's data database is, when not on the control database's server.
        $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS db_host text NULL, ADD COLUMN IF NOT EXISTS db_port int NULL, ADD COLUMN IF NOT EXISTS db_username text NULL, ADD COLUMN IF NOT EXISTS db_ssl_mode text NULL;",
        // Connection pool settings, and where the data was before a move (kept until the move is finished).
        $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS pool_max int NULL, ADD COLUMN IF NOT EXISTS command_timeout int NULL, ADD COLUMN IF NOT EXISTS previous jsonb NULL;",
        // The first project's data, once it has a database of its own (no row: it's in the control database).
        $"""
        CREATE TABLE IF NOT EXISTS {DefaultTable} (
            id text PRIMARY KEY, database text NOT NULL, db_host text NULL, db_port int NULL, db_username text NULL, db_ssl_mode text NULL,
            pool_max int NULL, command_timeout int NULL, previous jsonb NULL
        );
        """,
    ], ct);

    /// <summary>The default project (the server's own database).</summary>
    internal ProjectHost Default => _default ?? throw new InvalidOperationException("The server isn't running yet.");

    /// <summary>Every running project, the default first.</summary>
    internal IReadOnlyList<ProjectHost> All => [Default, .. _hosts.Values.OrderBy(h => h.Project.Id, StringComparer.Ordinal)];

    /// <summary>The project at <c>/p/&lt;id&gt;</c>, or null.</summary>
    internal ProjectHost? Find(string id) =>
        _hosts.TryGetValue(id, out var host) ? host : _default is { } d && d.CurrentId == id ? d : null;

    /// <summary>A project's schema in the control database, for its configuration.</summary>
    public static string ConfigSchema(string key) => "project_" + NotWord().Replace(key, "_");

    /// <summary>
    /// The secrets in every configuration schema (the server's, each project's, archived ones too) moved to the current
    /// master key where a previous one opens them; counts what no key opens. Safe to run any time.
    /// </summary>
    internal async Task<(int Secrets, int Reencrypted, int Unreadable)> ReencryptSecretsAsync(CancellationToken ct)
    {
        var schemas = new List<string> { "server", ConsoleAccounts.Schema, ConfigSchema(DefaultKey) };
        schemas.AddRange((await ListAsync(ct)).Select(r => ConfigSchema(r.Id)));
        int total = 0, moved = 0, unreadable = 0;
        foreach (var schema in schemas.Distinct())
        {
            var services = new ServiceCollection().AddLogging();
            services.AddWincheConfig(o =>
            {
                o.ConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = schema, Pooling = false }.ConnectionString;
                o.MasterKey = server.MasterKey;
                o.PreviousMasterKeys = server.PreviousMasterKeys;
            });
            await using var sp = services.BuildServiceProvider();
            try
            {
                var s = await sp.GetRequiredService<SecretStore>().ReencryptAsync(ct);
                (total, moved, unreadable) = (total + s.Current + s.Reencrypted + s.Unreadable, moved + s.Reencrypted, unreadable + s.Unreadable);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { }   // no secrets there yet
        }
        return (total, moved, unreadable);
    }

    private string ConfigConnection(string key) =>
        new NpgsqlConnectionStringBuilder(server.ConnectionString) { SearchPath = ConfigSchema(key) }.ConnectionString;

    /// <summary>The connection to a project's data database, wherever its row says it is.</summary>
    private Task<string> DataConnectionAsync(ProjectRow row, CancellationToken ct) =>
        ConnectionForAsync(row.Location, PasswordSecret(row.Id), ct);

    /// <summary>
    /// A connection to <paramref name="at"/>: on the control database's server with its user (no host), or on its own
    /// (its password the secret <paramref name="passwordSecret"/>); with its pool settings.
    /// </summary>
    internal async Task<string> ConnectionForAsync(DataLocation at, string passwordSecret, CancellationToken ct, string? password = null)
    {
        var b = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = at.Database, SearchPath = null };
        if (at.Host is not null)
        {
            b.Host = at.Host;
            if (at.Port is { } port) b.Port = port;
            if (at.Username is { } user) b.Username = user;
            b.Password = password ?? await settings.Secrets.GetAsync(passwordSecret, ct);
            if (at.SslMode is { } ssl && Enum.TryParse<SslMode>(ssl, true, out var mode)) b.SslMode = mode;
        }
        if (at.PoolMax is { } max) b.MaxPoolSize = max;
        if (at.CommandTimeout is { } timeout) b.CommandTimeout = timeout;
        return b.ConnectionString;
    }

    /// <summary>The control database's name (where the first project's data is until it moves).</summary>
    internal string ControlDatabase => new NpgsqlConnectionStringBuilder(server.ConnectionString).Database ?? "";

    private async Task<ServerProject> ProjectForAsync(ProjectRow row, CancellationToken ct) => new(
        row.Id,
        await DataConnectionAsync(row, ct),
        Path.Combine(server.ProjectsPath, row.Id, "files"),
        $"{settings.Current.PublicUrl.TrimEnd('/')}/p/{row.Id}",
        IsDefault: false,
        ConfigConnection(row.Id));

    private async Task<ServerProject> DefaultProjectAsync(CancellationToken ct)
    {
        var (at, _) = await DefaultLocationAsync(ct);
        var conn = at is null ? server.ConnectionString : await ConnectionForAsync(at, PasswordSecret(DefaultKey), ct);
        // The first project's files: under the projects' folder like the others, unless the deployment keeps its own.
        var files = string.IsNullOrWhiteSpace(server.FilesPath) ? Path.Combine(server.ProjectsPath, server.ProjectId, "files") : server.FilesPath;
        return new(server.ProjectId, conn, files, settings.Current.PublicUrl.TrimEnd('/'), IsDefault: true, ConfigConnection(DefaultKey));
    }

    private async Task<ProjectHost> StartAsync(ServerProject project, CancellationToken ct)
    {
        await ConfigSchemas.PrepareAsync(Db, project, ct);
        var (app, inProcess) = await ProjectApp.BuildAsync(server, settings, project, this, configuration, environment, ct);
        return new ProjectHost(project, app, inProcess);
    }

    private ServerSettings? _applied;

    public async Task StartAsync(CancellationToken ct)
    {
        await settings.Store.EnsureLoadedAsync(ct);   // the server settings in force, before any project is built with them
        _applied = settings.Current;
        settings.Area.Changed += next =>
        {
            var before = _applied!;
            _applied = next;
            if (NeedsRestart(before, next))
                _ = Task.Run(async () =>
                {
                    try { await RestartAllAsync(); }
                    catch (Exception ex) { logger.LogError(ex, "Couldn't restart the projects with the new server settings"); }
                });
        };
        await EnsureAsync(ct);
        _default = await StartAsync(await DefaultProjectAsync(ct), ct);
        foreach (var row in await ListAsync(ct))
        {
            if (row.ArchivedAt is not null) { _archived[row.Id] = true; continue; }
            try { _hosts[row.Id] = await StartAsync(await ProjectForAsync(row, ct), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken project doesn't keep the others down.
                logger.LogError(ex, "Couldn't start project {Project}", row.Id);
            }
        }
        // Changing the master key (a previous one deployed beside it): every secret, archived projects' too, moves to the
        // new key now; the log says when nothing needs the old one any more.
        if (server.PreviousMasterKeys.Length > 0)
        {
            var (secrets, moved, unreadable) = await ReencryptSecretsAsync(ct);
            if (unreadable > 0)
                logger.LogError("Master key: {Unreadable} of {Secrets} secrets open with no key this server has: deploy the key they were saved with as a previous key, or set them again.", unreadable, secrets);
            else
                logger.LogWarning("Master key: {Moved} secret(s) moved to the current key; all {Secrets} are on it. Remove Winche__PreviousMasterKeys and restart.", moved, secrets);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var host in _hosts.Values) await StopAsync(host, ct);
        if (_default is { } d) await StopAsync(d, ct);
    }

    private async Task StopAsync(ProjectHost host, CancellationToken ct)
    {
        try
        {
            await host.App.StopAsync(ct);
            await host.App.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Project {Project} didn't stop cleanly", host.Project.Id);
        }
    }

    public async Task<IReadOnlyList<ProjectRow>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = Db.CreateCommand($"SELECT id, name, database, created_at, archived_at, db_host, db_port, db_username, db_ssl_mode, pool_max, command_timeout, previous::text FROM {Table} ORDER BY created_at");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<ProjectRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11)));
        return rows;
    }

    private async Task<ProjectRow> RowAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(r => r.Id == id)
            ?? throw new ArgumentException(Default.CurrentId == id ? "The server's first project can't be archived or deleted." : $"There's no project {id}.");

    private async Task SetArchivedAsync(string id, bool archived, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand($"UPDATE {Table} SET archived_at = {(archived ? "now()" : "NULL")} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Archives a project: its address is refused at once, its app stops a moment later (the request asking for it
    /// may be its own), and its database and files stay until it's restored or deleted.
    /// </summary>
    public async Task ArchiveAsync(string id, CancellationToken ct = default)
    {
        await _creating.WaitAsync(ct);
        try
        {
            var row = await RowAsync(id, ct);
            if (row.ArchivedAt is not null) return;
            await SetArchivedAsync(id, true, ct);
            _archived[id] = true;
            if (_hosts.TryRemove(id, out var host))
                _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(1)); await StopAsync(host, CancellationToken.None); });
        }
        finally { _creating.Release(); }
    }

    /// <summary>Starts an archived project again, at its address.</summary>
    public async Task RestoreAsync(string id, CancellationToken ct = default)
    {
        await _creating.WaitAsync(ct);
        try
        {
            var row = await RowAsync(id, ct);
            if (row.ArchivedAt is null) return;
            _hosts[id] = await StartAsync(await ProjectForAsync(row, ct), ct);
            await SetArchivedAsync(id, false, ct);
            _archived.TryRemove(id, out _);
        }
        finally { _creating.Release(); }
    }

    /// <summary>Deletes an archived project: its database (dropped) and files, for good.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _creating.WaitAsync(ct);
        try
        {
            var row = await RowAsync(id, ct);
            if (row.ArchivedAt is null) throw new ArgumentException("Archive the project before deleting it.");
            await using (var maintenance = NpgsqlDataSource.Create(await MaintenanceConnectionAsync(row, ct)))
            await using (var drop = maintenance.CreateCommand($"DROP DATABASE IF EXISTS \"{row.Database}\" WITH (FORCE)"))
                await drop.ExecuteNonQueryAsync(ct);
            await settings.Secrets.DeleteAsync(PasswordSecret(id), ct);
            await using (var dropConfig = Db.CreateCommand($"DROP SCHEMA IF EXISTS \"{ConfigSchema(id)}\" CASCADE"))
                await dropConfig.ExecuteNonQueryAsync(ct);
            await using (var delete = Db.CreateCommand($"DELETE FROM {Table} WHERE id = $1"))
            {
                delete.Parameters.AddWithValue(id);
                await delete.ExecuteNonQueryAsync(ct);
            }
            _archived.TryRemove(id, out _);
            var folder = Path.Combine(server.ProjectsPath, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            logger.LogWarning("Deleted project {Project} (database {Database})", id, row.Database);
        }
        finally { _creating.Release(); }
    }

    /// <summary>The server a project's database is on, through its "postgres" database (to create or drop it).</summary>
    private async Task<string> MaintenanceConnectionAsync(ProjectRow row, CancellationToken ct) =>
        new NpgsqlConnectionStringBuilder(await DataConnectionAsync(row, ct)) { Database = "postgres" }.ConnectionString;

    /// <summary>
    /// Settings the projects are built with (the address, folders, Deno, two-step, sign-in length) changed: every
    /// project restarts in place with them. Its new app takes requests at once; the old one stops a moment later.
    /// </summary>
    internal async Task RestartAllAsync(CancellationToken ct = default)
    {
        await _creating.WaitAsync(ct);
        var retired = new List<ProjectHost>();
        try
        {
            if (_default is { } d)
            {
                _default = await StartAsync(await DefaultProjectAsync(ct), ct);
                retired.Add(d);
            }
            foreach (var row in (await ListAsync(ct)).Where(r => r.ArchivedAt is null))
            {
                var fresh = await StartAsync(await ProjectForAsync(row, ct), ct);
                if (_hosts.TryGetValue(row.Id, out var old)) retired.Add(old);
                _hosts[row.Id] = fresh;
            }
        }
        finally { _creating.Release(); }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            foreach (var old in retired) await StopAsync(old, CancellationToken.None);
        });
        logger.LogInformation("Server settings changed: {Count} project(s) restarted with them", retired.Count);
    }

    /// <summary>Whether a change needs the projects rebuilt (CORS and new projects' database are read as they're used).</summary>
    internal static bool NeedsRestart(ServerSettings before, ServerSettings after) =>
        before.PublicUrl != after.PublicUrl
        || before.RequireTwoFactorForSettings != after.RequireTwoFactorForSettings
;

    /// <summary>
    /// Creates a project: its database, its app (started), and its console's project (named, every product off).
    /// </summary>
    public async Task<ServerProject> CreateAsync(string name, string id, string? by, CancellationToken ct = default) =>
        await CreateAsync(name, id, null, by, ct);

    /// <summary>
    /// Where a new project's data goes: a database of its own on the control database's server, unless
    /// <paramref name="t"/> names another server (or another database name).
    /// </summary>
    private DataLocation NewLocation(string id, DataTarget? t)
    {
        var database = string.IsNullOrWhiteSpace(t?.Database) ? DatabaseName(id) : t.Database.Trim();
        return t is null || string.IsNullOrWhiteSpace(t.Host)
            ? new DataLocation(database)
            : new DataLocation(database, t.Host.Trim(), t.Port ?? 5432, string.IsNullOrWhiteSpace(t.Username) ? null : t.Username.Trim(), t.SslMode ?? "Prefer");
    }

    /// <summary>Whether a new project <paramref name="id"/> can have its data at <paramref name="t"/> (none: this server's PostgreSQL).</summary>
    public async Task<DataTargetCheck> CheckNewAsync(string id, DataTarget? t, CancellationToken ct = default)
    {
        var at = NewLocation(id, t);
        if (at.Database.Length > 63 || at.Database.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            return new(false, "Name the database: letters, digits and underscores, up to 63.", null, false, 0);
        if (at.Host is null && at.Database == ControlDatabase) return new(false, "That's the control database.", null, false, 0);
        return await ProbeAsync(at, t?.Password, ct);
    }

    /// <summary>
    /// Creates a project with its data where <paramref name="database"/> says (none: a database of its own on this
    /// server's PostgreSQL): its database (made unless it's there and empty), its app (started), and its console's project.
    /// </summary>
    public async Task<ServerProject> CreateAsync(string name, string id, DataTarget? database, string? by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name the project.");
        if (!Winche.Console.Project.ProjectSetup.IsValidId(id))
            throw new ArgumentException("The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.");
        await _creating.WaitAsync(ct);
        try
        {
            if (Find(id) is not null || (await ListAsync(ct)).Any(r => r.Id == id))
                throw new ArgumentException(IsArchived(id) ? $"There's an archived project {id}: restore it, or delete it first." : $"There's already a project {id}.");
            var at = NewLocation(id, database);
            var check = await CheckNewAsync(id, database, ct);
            if (!check.Ok) throw new ArgumentException(check.Problem);
            var row = new ProjectRow(id, name.Trim(), at.Database, DateTimeOffset.UtcNow, null, at.Host, at.Port, at.Username, at.SslMode);
            if (at.Host is not null && !string.IsNullOrEmpty(database?.Password)) await settings.Secrets.SetAsync(PasswordSecret(id), database.Password, by, ct);
            if (!check.Exists)
            {
                await using var maintenance = NpgsqlDataSource.Create(await MaintenanceConnectionAsync(row, ct));
                await using var create = maintenance.CreateCommand($"CREATE DATABASE \"{at.Database}\"");
                await create.ExecuteNonQueryAsync(ct);
            }
            await using (var insert = Db.CreateCommand($"INSERT INTO {Table} (id, name, database, created_at, db_host, db_port, db_username, db_ssl_mode) VALUES ($1, $2, $3, now(), $4, $5, $6, $7)"))
            {
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(name.Trim());
                insert.Parameters.AddWithValue(at.Database);
                insert.Parameters.AddWithValue((object?)row.Host ?? DBNull.Value);
                insert.Parameters.AddWithValue((object?)row.Port ?? DBNull.Value);
                insert.Parameters.AddWithValue((object?)row.Username ?? DBNull.Value);
                insert.Parameters.AddWithValue((object?)row.SslMode ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
            var host = await StartAsync(await ProjectForAsync(row, ct), ct);
            await host.App.Services.GetRequiredService<Winche.Console.Project.ProjectSetup>().CreateAsync(name, id, [], by, ct);
            _hosts[id] = host;
            return host.Project;
        }
        finally
        {
            _creating.Release();
        }
    }

    /// <summary>
    /// The first project's data, when first-run setup names it: a database of its own like any project's (none: on this
    /// server's PostgreSQL), never the server's own. The project restarts on it; what it logged before setup goes.
    /// </summary>
    public async Task PlaceFirstAsync(string id, DataTarget? database, IReadOnlyList<string> products, string? by, CancellationToken ct = default)
    {
        await _creating.WaitAsync(ct);
        try
        {
            if ((await DefaultLocationAsync(ct)).At is not null) throw new ArgumentException("The project already has its database.");
            var at = NewLocation(id, database);
            var check = await CheckNewAsync(id, database, ct);
            if (!check.Ok) throw new ArgumentException(check.Problem);
            if (at.Host is not null && !string.IsNullOrEmpty(database?.Password)) await settings.Secrets.SetAsync(PasswordSecret(DefaultKey), database.Password, by, ct);
            if (!check.Exists)
            {
                var conn = await ConnectionForAsync(at, PasswordSecret(DefaultKey), ct);
                await using var maintenance = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(conn) { Database = "postgres", Pooling = false }.ConnectionString);
                await using var create = maintenance.CreateCommand($"CREATE DATABASE \"{at.Database}\"");
                await create.ExecuteNonQueryAsync(ct);
            }
            await SaveLocationAsync(DefaultKey, at, null, ct);
            var old = _default;
            var next = await StartAsync(await DefaultProjectAsync(ct), ct);
            var runner = next.App.Services.GetRequiredService<Winche.Console.Project.ProductSetupRunner>();
            foreach (var product in products) await runner.SetUpNowAsync(product, ct);
            _default = next;
            if (old is not null)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await StopAsync(old, CancellationToken.None);
                    // Before setup it logged into the server's database: those tables go (the server's own stay).
                    try { await DataCopy.DropPreSetupAsync(Db, CancellationToken.None); }
                    catch (Exception ex) { logger.LogWarning(ex, "Couldn't drop the logs from before setup in the server's database"); }
                });
        }
        finally { _creating.Release(); }
    }

    /// <summary>
    /// The project's database: the server's own database's name, <c>_p_</c> and the id with underscores (so two servers
    /// sharing a PostgreSQL don't collide), at most PostgreSQL's 63 characters.
    /// </summary>
    public string DatabaseName(string id)
    {
        var serverDb = new NpgsqlConnectionStringBuilder(server.ConnectionString).Database ?? "winche";
        var suffix = "_p_" + NotWord().Replace(id, "_");
        return NotWord().Replace(serverDb.ToLowerInvariant(), "_")[..Math.Min(serverDb.Length, 63 - suffix.Length)] + suffix;
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotWord();
}

/// <summary>What a project's console sees of the server's projects (<see cref="Winche.Console.Project.IWincheProjects"/>).</summary>
internal sealed class ServerProjectsForConsole(ProjectHosts hosts, ServerProject project, ServerOptions server) : Winche.Console.Project.IWincheProjects
{
    public string CurrentPrefix => project.IsDefault ? "" : $"/p/{project.Id}";

    private async Task<Winche.Console.Project.WincheProjectInfo> InfoAsync(ProjectHost host, CancellationToken ct)
    {
        var current = host.App.Services.GetRequiredService<Winche.Console.Project.ProjectSetup>().Current;
        var image = await host.App.Services.GetRequiredService<Winche.Console.Project.ProjectImageStore>().VersionAsync(ct);
        return new(current.Id, current.Name, $"{(host.Project.IsDefault ? "" : $"/p/{host.Project.Id}")}{server.ConsolePath}", image, host.Project.IsDefault)
        {
            Products = [.. Winche.Console.Project.WincheProducts.All.Where(current.IsStarted)],
        };
    }

    public async Task<IReadOnlyList<Winche.Console.Project.WincheProjectInfo>> ListAsync(Winche.Console.Project.WincheProjectsCaller caller, CancellationToken ct)
    {
        var list = new List<Winche.Console.Project.WincheProjectInfo>();
        foreach (var host in hosts.All)
            if (await MayUseAsync(host, caller, ct)) list.Add(await InfoAsync(host, ct));
        return list;
    }

    /// <summary>Server admins use every project; the first goes by the account's role; the others by their members.</summary>
    private static async Task<bool> MayUseAsync(ProjectHost host, Winche.Console.Project.WincheProjectsCaller caller, CancellationToken ct)
    {
        if (caller.ServerAdmin) return true;
        if (host.Project.IsDefault) return caller.HasAccountRole;
        return caller.UserId is { } id && host.App.Services.GetService<Winche.Console.Project.ProjectMembers>() is { } members
            && await members.RoleOfAsync(id, ct) is not null;
    }

    public async Task<Winche.Console.Project.WincheProjectInfo> CreateAsync(string name, string id, Winche.Console.Project.DataMoveTarget? database, string? by, CancellationToken ct)
    {
        await hosts.CreateAsync(name, id, Target(database), by, ct);
        return await InfoAsync(hosts.Find(id)!, ct);
    }

    public async Task<bool> PlaceFirstAsync(string id, Winche.Console.Project.DataMoveTarget? database, IReadOnlyList<string> products, string? by, CancellationToken ct)
    {
        if (!project.IsDefault) return false;
        await hosts.PlaceFirstAsync(id, Target(database), products, by, ct);
        return true;
    }

    public async Task<Winche.Console.Project.DataMoveCheck> CheckDatabaseAsync(string id, Winche.Console.Project.DataMoveTarget? database, CancellationToken ct)
    {
        var c = await hosts.CheckNewAsync(id, Target(database), ct);
        return new(c.Ok, c.Problem, c.Version, c.Exists, 0);
    }

    private static DataTarget? Target(Winche.Console.Project.DataMoveTarget? t) =>
        t is null ? null : new DataTarget(t.Database ?? "", t.Host, t.Port, t.Username, t.Password, t.SslMode);

    public async Task<IReadOnlyList<Winche.Console.Project.WincheArchivedProject>> ListArchivedAsync(CancellationToken ct) =>
        [.. (await hosts.ListAsync(ct)).Where(r => r.ArchivedAt is not null).Select(r => new Winche.Console.Project.WincheArchivedProject(r.Id, r.Name, r.ArchivedAt!.Value))];

    public Task ArchiveAsync(string id, string? by, CancellationToken ct) => hosts.ArchiveAsync(id, ct);
    public Task RestoreAsync(string id, CancellationToken ct) => hosts.RestoreAsync(id, ct);
    public Task DeleteAsync(string id, CancellationToken ct) => hosts.DeleteAsync(id, ct);
}

/// <summary>
/// Sends each request to its project: <c>/p/&lt;id&gt;/…</c> to that project (seen at that path), anything else to
/// the default project (as before projects). <c>/healthz</c> is the server's own.
/// </summary>
internal static class ProjectRouter
{
    public static async Task RouteAsync(HttpContext http, ProjectHosts projects)
    {
        var path = http.Request.Path;
        if (path.StartsWithSegments("/p", out var rest) && rest.HasValue && rest.Value!.Length > 1)
        {
            var value = rest.Value!;
            var slash = value.IndexOf('/', 1);
            var id = slash < 0 ? value[1..] : value[1..slash];
            if (projects.IsArchived(id))
            {
                await Results.Json(new { error = new { code = "failed-precondition", message = $"Project {id} is archived: a server admin can restore it.", reason = "project-archived" } }, statusCode: 410).ExecuteAsync(http);
                return;
            }
            if (projects.Find(id) is not { } host)
            {
                await Results.Json(new { error = new { code = "not-found", message = $"There's no project {id} on this server." } }, statusCode: 404).ExecuteAsync(http);
                return;
            }
            if (await PausedAsync(http, projects, host)) return;
            var pathBase = http.Request.PathBase.Add($"/p/{id}");
            await host.Server.HandleAsync(http, pathBase, slash < 0 ? PathString.Empty : new PathString(value[slash..]));
            return;
        }
        if (await PausedAsync(http, projects, projects.Default)) return;
        await projects.Default.Server.HandleAsync(http, http.Request.PathBase, path);
    }

    /// <summary>A project whose data is moving is paused: requests are told to try again, with how far the move has got.</summary>
    private static async Task<bool> PausedAsync(HttpContext http, ProjectHosts projects, ProjectHost host)
    {
        if (projects.MovingOf(host) is not { } move) return false;
        http.Response.Headers.RetryAfter = "3";
        await Results.Json(new
        {
            error = new { code = "unavailable", message = "This project's data is moving to another database: try again in a moment.", reason = "project-moving" },
            move = new { stage = move.Stage, table = move.Table, tablesDone = move.TablesDone, tablesTotal = move.TablesTotal, rows = move.Rows, startedAt = move.StartedAt },
        }, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(http);
        return true;
    }
}
