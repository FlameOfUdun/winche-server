using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Winche.Server.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // Each test runs whole servers, each project with its own pools: more connections than PostgreSQL's 100.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=500").Build();
    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"s{Guid.NewGuid():N}"[..20];
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>The server as a container runs it: configuration from "Winche:*" only.</summary>
public sealed class ServerFactory(string connection, string? files, params (string Key, string Value)[] extra) : WebApplicationFactory<Program>
{
    /// <summary>The first-run setup code the test servers use (a deployment's Winche__SetupCode).</summary>
    public const string SetupCode = "TEST-SETUP-CODE";

    /// <summary>A running project's own services (its products), for checking what an API call did.</summary>
    internal IServiceProvider ProjectServices(string id) =>
        Services.GetRequiredService<ProjectHosts>().Find(id)?.App.Services ?? throw new InvalidOperationException($"Project {id} isn't running.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (k, v) in extra) builder.UseSetting(k, v);
        builder.UseEnvironment("Production");
        builder.UseSetting("Winche:ConnectionString", connection);
        if (!extra.Any(e => e.Key == "Winche:MasterKey")) builder.UseSetting("Winche:MasterKey", "server-tests-master-key-0123456789abcdefghij");
        builder.UseSetting("Winche:PublicUrl", "http://localhost");
        // AWS out of reach: tests never call the real one (what a key may do is asked of a port nobody answers on).
        if (!extra.Any(e => e.Key == "Winche:SecretsAwsServiceUrl")) builder.UseSetting("Winche:SecretsAwsServiceUrl", "http://127.0.0.1:9");
        builder.UseSetting("Winche:FunctionPackagesPath", Path.Combine(AppContext.BaseDirectory, "SdkPackages"));   // the SDKs the tests carry
        if (!extra.Any(e => e.Key == "Winche:SetupCode")) builder.UseSetting("Winche:SetupCode", SetupCode);
        if (files is not null) builder.UseSetting("Winche:ProjectsPath", files);   // projects' files under this folder
    }
}

/// <summary>Projects for tests: made on the server's console (as its signed-in admin), products started and set up.</summary>
internal static class TestProjects
{
    public static async Task CreateAsync(HttpClient console, string id, string name, params string[] products)
    {
        var made = await console.PostAsJsonAsync("/console/v1/projects", new { name, id });
        Assert.True(made.IsSuccessStatusCode, $"{(int)made.StatusCode} {await made.Content.ReadAsStringAsync()}");
        await StartAsync(console, id, products);
    }

    public static async Task StartAsync(HttpClient console, string id, params string[] products)
    {
        foreach (var product in products.Where(p => p != "hooks"))   // hooks come with the products that send them
            await ServerTests.StartProductAsync(console, $"/projects/{id}", product);
    }
}

[Collection("postgres")]
public class ServerTests(PostgresFixture pg)
{
    private static string B64(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public void Forwarded_headers_are_believed_from_private_networks_unless_told_otherwise()
    {
        static bool Trusted(ServerOptions o, string address) => o.ProxyNetworks()!.Any(n => n.Contains(System.Net.IPAddress.Parse(address)));
        var standard = new ServerOptions();
        Assert.True(Trusted(standard, "172.17.0.1"));   // Docker's bridge
        Assert.True(Trusted(standard, "10.0.4.2"));
        Assert.True(Trusted(standard, "fd00::5"));
        Assert.False(Trusted(standard, "35.191.10.20"));   // a cloud load balancer: set it
        var cloud = new ServerOptions { TrustedProxies = ["35.191.0.0/16", "130.211.0.5"] };
        Assert.True(Trusted(cloud, "35.191.10.20"));
        Assert.True(Trusted(cloud, "130.211.0.5"));
        Assert.False(Trusted(cloud, "10.0.4.2"));   // named ones replace the default
        Assert.Null(new ServerOptions { TrustedProxies = ["*"] }.ProxyNetworks());
        Assert.Contains(new ServerOptions { MasterKey = new string('k', 40), PublicUrl = "https://api.example.com", ConnectionString = "Host=x",
            TrustedProxies = ["proxy.example.com"] }.Problems(), p => p.Contains("TrustedProxies"));
    }

    [Fact]
    public void Missing_settings_are_named_with_how_to_set_them()
    {
        var problems = new ServerOptions { MasterKey = "short", PublicUrl = "api.example.com" }.Problems();
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("Winche__ConnectionString"));
        Assert.Contains(problems, p => p.StartsWith("Winche__MasterKey") && p.Contains("openssl rand"));
        Assert.Contains(problems, p => p.StartsWith("Winche__PublicUrl"));
    }

    [Fact]
    public async Task Day_one_set_up_the_console_publish_rules_and_an_app_signs_up_and_writes()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files);
        var app = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/livez")).StatusCode);
        HttpResponseMessage health = null!;
        for (var i = 0; i < 50 && (health = await app.GetAsync("/healthz")).StatusCode != HttpStatusCode.OK; i++) await Task.Delay(100);   // ready once warm
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("/console", (await app.GetAsync("/")).Headers.Location?.ToString());
        // The server runs with no project at all: apps' APIs are a project's, at /projects/<id>; the root says so.
        var none = await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Contains("/projects/<project id>/auth/v1/signUp", await none.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync("/projects/clinic/.well-known/winche")).StatusCode);

        // The first admin sets the server up (an account, nothing else), then makes a project on its Projects page.
        var console = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" })).StatusCode);
        var state = await Json(await console.GetAsync("/console/v1/session/state"));
        Assert.True(state.GetProperty("server").GetBoolean());
        Assert.Equal(0, (await Json(await console.GetAsync("/console/v1/projects"))).GetProperty("projects").GetArrayLength());
        // Before a product starts, its API refuses.
        await Json(await console.PostAsJsonAsync("/console/v1/projects", new { name = "Clinic", id = "clinic" }));
        var off = await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("service-disabled", await off.Content.ReadAsStringAsync());
        await TestProjects.StartAsync(console, "clinic", "database", "storage", "authentication", "functions");
        var setup = System.Text.Json.JsonDocument.Parse(await app.GetStringAsync("/projects/clinic/.well-known/winche")).RootElement;
        Assert.Equal("clinic", setup.GetProperty("projectId").GetString());
        Assert.Equal(["database", "storage", "auth", "functions"],
            setup.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray());

        // An app user signs up with Winche Auth (email and password are on to start with)…
        var session = await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        var uid = session.GetProperty("user").GetProperty("uid").GetString()!;
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var doc = new { fields = new { name = new { stringValue = "Ann" } } };
        // …but nothing is allowed until rules are published.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PutAsJsonAsync($"/projects/clinic/documents/v1/{B64($"users/{uid}")}", doc)).StatusCode);

        // Every API refuses the same way: { error: { code, message, reason? } }, one list of codes, one HTTP table.
        async Task<(int, string, string?)> Refusal(HttpResponseMessage r)
        {
            var e = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
            Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString()));
            return ((int)r.StatusCode, e.GetProperty("code").GetString()!, e.TryGetProperty("reason", out var why) ? why.GetString() : null);
        }
        Assert.Equal((403, "permission-denied", null), await Refusal(await app.PutAsJsonAsync($"/projects/clinic/documents/v1/{B64($"users/{uid}")}", doc)));
        Assert.Equal((403, "permission-denied", null), await Refusal(await app.PutAsJsonAsync($"/projects/clinic/files/v1/{B64("a/b.txt")}", new { mimeType = "text/plain", sizeBytes = 1 })));
        Assert.Equal((401, "unauthenticated", "INVALID_CREDENTIALS"), await Refusal(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@example.com", password = "wrong one, long enough" })));
        Assert.Equal((404, "not-found", null), await Refusal(await app.PostAsJsonAsync("/projects/clinic/functions/v1/call/nothing", new { data = (object?)null })));

        // The admin publishes rules in the console.
        var parsed = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/text/parse",
            new { text = "match users/{uid} { allow read, write: if request.auth.uid == uid; }" }));
        await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/database", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Users own their document" }));

        Assert.Equal(HttpStatusCode.OK, (await app.PutAsJsonAsync($"/projects/clinic/documents/v1/{B64($"users/{uid}")}", doc)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PutAsJsonAsync($"/projects/clinic/documents/v1/{B64("users/someone-else")}", doc)).StatusCode);

        // Console sign-in survives a restart: its cookie keys are in the database, not the container.
        await using var conn = new NpgsqlConnection(server.Services.GetRequiredService<ServerOptions>().ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM winche_server_keys", conn);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Apps_apis_and_the_consoles_files_are_compressed_the_consoles_api_never()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files);
        var http = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await Json(await http.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await http.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(http, "clinic", "Clinic", "database");

        async Task<string?> Encoding(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.ParseAdd("br, gzip");
            using var response = await http.SendAsync(request);
            return response.Content.Headers.ContentEncoding.FirstOrDefault();
        }
        // The apps' APIs (a bearer token, never a cookie) and the console's page and files: compressed.
        Assert.Equal("br", await Encoding("/projects/clinic/documents/v1/" + Convert.ToBase64String("notes/n1"u8.ToArray())));
        Assert.Equal("br", await Encoding("/console/"));
        using (var query = new HttpRequestMessage(HttpMethod.Post, "/projects/clinic/documents/v1:runQuery") { Content = JsonContent.Create(new { query = new { collection = "notes" } }) })
        {
            query.Headers.AcceptEncoding.ParseAdd("br");
            using var answer = await http.SendAsync(query);
            Assert.Equal("br", answer.Content.Headers.ContentEncoding.FirstOrDefault());   // the colon verbs too
        }
        // A console cookie of a server before 3.0 (path /) beside this one doesn't sign anyone out.
        using (var stale = new HttpRequestMessage(HttpMethod.Get, "/console/v1/session/state"))
        {
            stale.Headers.Add("Cookie", "WincheConsole=CfDJ8stale-session-of-2x");
            using var answer = await http.SendAsync(stale);
            Assert.Equal(JsonValueKind.Object, (await Json(answer)).GetProperty("user").ValueKind);
        }
        // The console's API and sign-in (cookies): never, so an answer's size can't give away what it holds.
        Assert.Null(await Encoding("/console/v1/session/state"));
        Assert.Null(await Encoding("/console/v1/projects/clinic/project"));
        Assert.Null(await Encoding("/projects/clinic/auth/v1/config"));
    }

    [Fact]
    public void Apps_and_a_projects_console_calls_go_to_the_project_the_rest_to_the_servers_console()
    {
        Assert.Equal(("/projects", "clinic", "/documents/v1/notes"), Route("/projects/clinic/documents/v1/notes"));
        Assert.Equal(("/projects", "clinic", ""), Route("/projects/clinic"));
        Assert.Equal(("/console/v1/projects", "clinic", "/documents/query"), Route("/console/v1/projects/clinic/documents/query"));
        // What's about a project as a whole is the server's: the project itself, archiving, restoring.
        Assert.Null(Route("/console/v1/projects/clinic"));
        Assert.Null(Route("/console/v1/projects/clinic/archive"));
        Assert.Null(Route("/console/v1/projects/clinic/restore"));
        Assert.Null(Route("/console/v1/projects"));
        Assert.Null(Route("/console/projects/clinic/data"));   // the console's pages
        Assert.Null(Route("/p/clinic/documents/v1/notes"));   // the old address: nothing there
        Assert.Null(Route("/projectsx/clinic"));
        // Compressed: apps' APIs and the console's page and files; never the console's API (cookies).
        Assert.True(ProjectApp.Compressible("/documents/v1/notes"));
        Assert.True(ProjectApp.ConsoleCompressible("/console/assets/index.js"));
        Assert.False(ProjectApp.ConsoleCompressible("/console/v1/session/state"));
    }

    private static (string, string, string)? Route(string path) =>
        ProjectRouter.Target(path) is var (prefix, id, under) ? (prefix, id, under.Value ?? "") : null;

    [Fact]
    public async Task A_project_whose_start_fails_is_undone_so_a_retry_works_and_file_errors_say_what_to_do()
    {
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var db = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(db, null, ("Winche:ProjectsPath", projectsPath));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));

        // The project's folder can't be made (a file is in its place): its start fails, said plainly, not as a bare 500.
        var blocker = Path.Combine(projectsPath, "crm");
        File.WriteAllText(blocker, "in the way");
        var failed = await console.PostAsJsonAsync("/console/v1/projects", new { name = "CRM", id = "crm" });
        var text = await failed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Contains("can't use its files", text);

        // Nothing is left of it: no row, no database, not listed; the retry makes it.
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var rows = new NpgsqlCommand("SELECT count(*) FROM winche_server_projects WHERE id = 'crm'", conn);
            Assert.Equal(0L, (long)(await rows.ExecuteScalarAsync())!);
            await using var dbs = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE '%_p_crm'", conn);
            Assert.Equal(0L, (long)(await dbs.ExecuteScalarAsync())!);
        }
        Assert.True(File.Exists(blocker), "a folder that was there before isn't the create's to remove");
        File.Delete(blocker);
        await TestProjects.CreateAsync(console, "crm", "CRM");
        Assert.Contains("crm", await (await console.GetAsync("/console/v1/projects")).Content.ReadAsStringAsync());
    }

    [Fact]
    public void Unwritable_folders_are_found_with_what_to_do()
    {
        Assert.Empty(Winche.Console.Api.ServerFolders.Problems([Directory.CreateTempSubdirectory("winche-ok-").FullName]));
        if (OperatingSystem.IsWindows() || Winche.Console.Api.ServerFolders.Uid == "0") return;   // root writes anywhere
        var locked = Directory.CreateTempSubdirectory("winche-locked-").FullName;
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var problem = Assert.Single(Winche.Console.Api.ServerFolders.Problems([locked, ""]));
            Assert.Contains($"{locked} isn't writable by uid {Winche.Console.Api.ServerFolders.Uid}", problem);
            Assert.Contains("chown -R", problem);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task Integrity_changes_need_two_step_sign_in_where_settings_do()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");
        foreach (var refused in new[]
        {
            await console.PostAsJsonAsync("/console/v1/projects/clinic/integrity/apps", new { name = "App", id = "my-app" }),
            await console.PutAsJsonAsync("/console/v1/projects/clinic/integrity/enforcement", new { enforcement = new Dictionary<string, string> { ["database"] = "off" }, reason = "x" }),
            await console.PostAsJsonAsync("/console/v1/projects/clinic/integrity/debug-tokens", new { app = "my-app", name = "CI" }),
        })
            Assert.Contains("TWO_FACTOR_REQUIRED", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await console.GetAsync("/console/v1/projects/clinic/integrity")).StatusCode);   // reading needs none
    }

    [Fact]
    public async Task Any_origin_may_call_and_a_projects_Integrity_tokens_work_only_in_it()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");
        await Json(await console.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));
        await StartProductAsync(console, "/projects/wards", "authentication");

        // CORS: any origin (a bearer token in a header, never a cookie; which apps may call is Integrity's).
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/projects/clinic/auth/v1/signIn");
        preflight.Headers.Add("Origin", "https://anything.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var allowed = await server.CreateClient().SendAsync(preflight);
        Assert.Equal("*", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        // Web apps may read when to try again and the server's clock.
        var cross = new HttpRequestMessage(HttpMethod.Post, "/projects/clinic/auth/v1/signIn/anonymous");
        cross.Headers.Add("Origin", "https://anything.example.com");
        var exposed = string.Join(",", (await server.CreateClient().SendAsync(cross)).Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("Retry-After", exposed);
        Assert.Contains("Date", exposed);

        // The same app id and a debug token in both projects: clinic's Integrity token is refused by wards.
        async Task<string> TokenAsync(string project)
        {
            await Json(await console.PostAsJsonAsync($"/console/v1/projects/{project}/integrity/apps", new { name = "Shared", id = "shared-app" }));
            var debug = (await Json(await console.PostAsJsonAsync($"/console/v1/projects/{project}/integrity/debug-tokens", new { app = "shared-app", name = "CI" }))).GetProperty("token").GetString();
            return (await Json(await server.CreateClient().PostAsJsonAsync($"/projects/{project}/integrity/v1/exchange", new { app = "shared-app", provider = "debug", debugToken = debug }))).GetProperty("token").GetString()!;
        }
        var clinic = await TokenAsync("clinic");
        await TokenAsync("wards");
        await Json(await console.PutAsJsonAsync("/console/v1/projects/wards/integrity/enforcement",
            new { enforcement = new Dictionary<string, string> { ["authentication"] = "enforce" }, reason = "Only our apps" }));
        var request = new HttpRequestMessage(HttpMethod.Post, "/projects/wards/auth/v1/signIn/anonymous");
        request.Headers.Add("X-Winche-Integrity", clinic);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.CreateClient().SendAsync(request)).StatusCode);
        // The key set and discovery document other services check tokens with stay open, enforced or not.
        Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().GetAsync("/projects/wards/.well-known/jwks.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().GetAsync("/projects/wards/.well-known/openid-configuration")).StatusCode);
        // Clinic itself isn't enforced: it answers with or without a token.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await server.CreateClient().PostAsync("/projects/clinic/auth/v1/signIn/anonymous", null)).StatusCode);

        // The database's colon verbs (writes, queries) are the database's too: enforced like the rest of it.
        await StartProductAsync(console, "/projects/wards", "database");
        await Json(await console.PutAsJsonAsync("/console/v1/projects/wards/integrity/enforcement",
            new { enforcement = new Dictionary<string, string> { ["database"] = "enforce" }, reason = "Only our apps" }));
        var commit = await server.CreateClient().PostAsJsonAsync("/projects/wards/documents/v1:commit", new { writes = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, commit.StatusCode);
        Assert.Contains("integrity-required", await commit.Content.ReadAsStringAsync());
        var query = await server.CreateClient().PostAsJsonAsync("/projects/wards/documents/v1:runQuery", new { });
        Assert.Contains("integrity-required", await query.Content.ReadAsStringAsync());

        // A change on the console's cookie that a browser says came from another site is refused; the console's own pass.
        var forged = new HttpRequestMessage(HttpMethod.Post, "/console/v1/projects/wards/integrity/debug-tokens") { Content = JsonContent.Create(new { app = "shared-app", name = "Evil" }) };
        forged.Headers.Add("Sec-Fetch-Site", "same-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await console.SendAsync(forged)).StatusCode);
        var fromOrigin = new HttpRequestMessage(HttpMethod.Post, "/console/v1/projects/wards/integrity/debug-tokens") { Content = JsonContent.Create(new { app = "shared-app", name = "Evil" }) };
        fromOrigin.Headers.Add("Origin", "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await console.SendAsync(fromOrigin)).StatusCode);
        var own = new HttpRequestMessage(HttpMethod.Post, "/console/v1/projects/wards/integrity/debug-tokens") { Content = JsonContent.Create(new { app = "shared-app", name = "Laptop" }) };
        own.Headers.Add("Sec-Fetch-Site", "same-origin");
        own.Headers.Add("X-Winche-Console", "1");
        var laptop = await Json(await console.SendAsync(own));

        // Restoring an older version keeps today's debug tokens: one removed since doesn't come back.
        var history = await Json(await console.GetAsync("/console/v1/projects/wards/auth/settings/history?area=integrity"));
        var withLaptop = history.GetProperty("versions")[0].GetProperty("id").GetInt64();
        await console.DeleteAsync($"/console/v1/projects/wards/integrity/debug-tokens/{laptop.GetProperty("id").GetString()}");
        await Json(await console.PostAsJsonAsync($"/console/v1/projects/wards/auth/settings/history/{withLaptop}/restore", new { reason = "Undo" }));
        var exchanged = await server.CreateClient().PostAsJsonAsync("/projects/wards/integrity/v1/exchange",
            new { app = "shared-app", provider = "debug", debugToken = laptop.GetProperty("token").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, exchanged.StatusCode);
    }

    [Fact]
    public async Task Projects_live_side_by_side_under_their_paths_sharing_nothing_but_the_console_sign_in()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var db = await pg.NewDatabaseAsync();
        (string, string) projectsSetting = ("Winche:ProjectsPath", projectsPath);
        await using (var server = new ServerFactory(db, files, projectsSetting))
        {
            var console = server.CreateClient();
            await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
            await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
            await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");

            // A second project, made from the console's switcher: its own database, made by the server.
            Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/console/v1/projects", new { name = "Again", id = "clinic" })).StatusCode);
            var created = await Json(await console.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));
            Assert.Equal("/console/projects/wards", created.GetProperty("consoleUrl").GetString());
            // Its pages are the console's, served once at /console.
            Assert.Contains("<base href=\"/console/\">", await console.GetStringAsync("/console/projects/wards/overview"));   // one console for every project

            // A project's console calls are its own areas only: signing in, the projects and the accounts are the server's.
            foreach (var serverArea in new[] { "projects", "session/state", "accounts", "invites", "server/settings" })
                Assert.Equal(HttpStatusCode.NotFound, (await console.GetAsync($"/console/v1/projects/wards/{serverArea}")).StatusCode);
            // The console lists them all, with their consoles' addresses.
            var listed = await Json(await console.GetAsync("/console/v1/projects"));
            Assert.True(listed.GetProperty("canCreate").GetBoolean());
            Assert.Equal(["clinic:/console/projects/clinic", "wards:/console/projects/wards"], listed.GetProperty("projects").EnumerateArray()
                .Select(p => $"{p.GetProperty("id").GetString()}:{p.GetProperty("consoleUrl").GetString()}").ToArray());
            // With the products each has started (the Projects home shows them).
            Assert.Equal(["clinic:authentication", "wards:"], listed.GetProperty("projects").EnumerateArray()
                .Select(p => $"{p.GetProperty("id").GetString()}:{string.Join(",", p.GetProperty("products").EnumerateArray().Select(x => x.GetString()))}").ToArray());
            // Every project is at its own path, seen from anywhere.
            Assert.Equal("/console/projects/clinic", (await Json(await console.GetAsync("/console/v1/projects"))).GetProperty("projects")[0].GetProperty("consoleUrl").GetString());

            // The same console sign-in works there; the project is made, named, with nothing started.
            var wards = await Json(await console.GetAsync("/console/v1/projects/wards/project"));
            Assert.True(wards.GetProperty("created").GetBoolean());
            Assert.Equal("Wards", wards.GetProperty("name").GetString());
            Assert.All(wards.GetProperty("products").EnumerateArray(), p => Assert.False(p.GetProperty("started").GetBoolean()));
            await StartProductAsync(console, "/projects/wards", "authentication");

            // The default project answers at the root (as before) and at its own path.
            Assert.Equal("clinic", (await Json(await console.GetAsync("/console/v1/projects/clinic/project"))).GetProperty("id").GetString());
            var wellKnown = await Json(await console.GetAsync("/projects/wards/.well-known/winche"));
            Assert.Equal("wards", wellKnown.GetProperty("projectId").GetString());
            Assert.Equal("http://localhost/projects/wards", wellKnown.GetProperty("baseUrl").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await console.GetAsync("/projects/nope/.well-known/winche")).StatusCode);

            // Users are each project's own: one signed up in Clinic doesn't exist in Wards, and the same email signs
            // up again there as somebody else.
            var app = server.CreateClient();
            var ann = await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
            Assert.NotEqual(HttpStatusCode.OK, (await app.PostAsJsonAsync("/projects/wards/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" })).StatusCode);
            var annInWards = await Json(await app.PostAsJsonAsync("/projects/wards/auth/v1/signUp", new { email = "ann@example.com", password = "another horse battery" }));
            Assert.NotEqual(ann.GetProperty("user").GetProperty("uid").GetString(), annInWards.GetProperty("user").GetProperty("uid").GetString());

            // Settings are each project's own too.
            await Json(await console.PutAsJsonAsync("/console/v1/projects/wards/project", new { name = "Ward boards", id = "wards" }));
            Assert.Equal("Clinic", (await Json(await console.GetAsync("/console/v1/projects/clinic/project"))).GetProperty("name").GetString());
        }

        // After a restart, the server starts every project again.
        await using (var again = new ServerFactory(db, files, projectsSetting))
        {
            var console = again.CreateClient();
            await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
            Assert.Equal("Ward boards", (await Json(await console.GetAsync("/console/v1/projects/wards/project"))).GetProperty("name").GetString());
            Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/projects/wards/auth/v1/signIn", new { email = "ann@example.com", password = "another horse battery" })).StatusCode);
        }
    }

    [Fact]
    public async Task A_created_project_has_its_own_members_and_others_see_only_their_projects()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));

        // The server admin is an admin of the new project without being listed; it has no members yet.
        var members = await Json(await admin.GetAsync("/console/v1/projects/wards/members"));
        Assert.Equal(0, members.GetProperty("members").GetArrayLength());
        Assert.Equal("admin@example.com", members.GetProperty("serverAdmins")[0].GetProperty("email").GetString());
        Assert.Equal(0, (await Json(await admin.GetAsync("/console/v1/projects/clinic/members"))).GetProperty("members").GetArrayLength());   // every project has its list

        // Bob is new: added as a Member, he gets an account and a link to set his password (no email set up here).
        var added = await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/members", new { email = "bob@example.com", role = "Member" }));
        Assert.True(added.GetProperty("newAccount").GetBoolean());
        var link = new Uri(added.GetProperty("link").GetString()!);
        // Console accounts are the server's: their links open the server's console (…/console/reset-password?mode=resetPassword&code=…).
        Assert.Equal("/console/reset-password", link.AbsolutePath);
        var code = System.Web.HttpUtility.ParseQueryString(link.Query)["code"];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/console/v1/session/reset-password",
            new { code, newPassword = "Bob-Passw0rd!long" })).StatusCode);

        var bob = server.CreateClient();
        await Json(await bob.PostAsJsonAsync("/console/v1/session/login", new { email = "bob@example.com", password = "Bob-Passw0rd!long" }));
        var here = await Json(await bob.GetAsync("/console/v1/projects/wards/access"));
        Assert.Equal("Member", here.GetProperty("role").GetString());
        Assert.True(here.GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/console/v1/projects/wards/project")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/console/v1/projects/wards/members")).StatusCode);   // members aren't admins

        // In clinic he's nobody: signed in, no role, refused. The server's own console he may use (his projects).
        var there = await Json(await bob.GetAsync("/console/v1/projects/clinic/access"));
        Assert.False(there.GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/console/v1/projects/clinic/project")).StatusCode);
        Assert.True((await Json(await bob.GetAsync("/console/v1/session/state"))).GetProperty("user").GetProperty("member").GetBoolean());

        // His switcher lists only Wards, and he can't create projects.
        var mine = await Json(await bob.GetAsync("/console/v1/projects"));
        Assert.Equal(["wards"], mine.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.False(mine.GetProperty("canCreate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/console/v1/projects", new { name = "Mine", id = "mine" })).StatusCode);

        // winche login signs in once, at the server's console: he approves there though he has no role in the first
        // project, and the CLI's session works in Wards with his role there (and lists his projects at the root).
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var approved = await Json(await bob.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 9005, state = "s", name = "winche CLI on laptop" }));
        var cliCode = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var session = await Json(await server.CreateClient().PostAsJsonAsync("/console/v1/cli/token", new { code = cliCode, verifier }));
        var cli = server.CreateClient();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/console/v1/projects/wards/project")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/console/v1/projects/wards/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/console/v1/projects/clinic/project")).StatusCode);
        Assert.Equal(["wards"], (await Json(await cli.GetAsync("/console/v1/projects"))).GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        // The same token works for @winche-technologies/winche-admin from another server (initializeAdmin): his role in Wards limits it.
        await StartProductAsync(admin, "/projects/wards", "database");
        var note = new { op = "db.add", args = new { collection = "notes", fields = new { by = new { stringValue = "bob's script" } } } };
        var noted = await Json(await cli.PostAsJsonAsync("/console/v1/projects/wards/admin", note));
        Assert.StartsWith("notes/", noted.GetProperty("result").GetProperty("document").GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.PostAsJsonAsync("/console/v1/projects/clinic/admin", note)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().PostAsJsonAsync("/console/v1/projects/wards/admin", note)).StatusCode);

        // The server admin's CLI makes projects (winche projects:create).
        var adminApproved = await Json(await admin.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 9005, state = "a", name = "winche CLI on desk" }));
        var adminCode = System.Web.HttpUtility.ParseQueryString(new Uri(adminApproved.GetProperty("redirect").GetString()!).Query)["code"];
        var adminSession = await Json(await server.CreateClient().PostAsJsonAsync("/console/v1/cli/token", new { code = adminCode, verifier }));
        var adminCli = server.CreateClient();
        adminCli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSession.GetProperty("accessToken").GetString());
        Assert.Equal("beds", (await Json(await adminCli.PostAsJsonAsync("/console/v1/projects", new { name = "Beds", id = "beds" }))).GetProperty("id").GetString());
        // Its settings and service accounts need two-step sign-in like the browser's (winche 2fa enable).
        var refused = await adminCli.PostAsJsonAsync("/console/v1/projects/beds/service-accounts", new { name = "gitlab", scopes = new[] { "rules" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("TWO_FACTOR_REQUIRED", await refused.Content.ReadAsStringAsync());

        // Made an Admin of Wards, he manages its members; removed, he's out.
        var bobId = (await Json(await admin.GetAsync("/console/v1/projects/wards/members"))).GetProperty("members")[0].GetProperty("userId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/console/v1/projects/wards/members/{bobId}", new { role = "Admin" })).StatusCode);
        await Task.Delay(100);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/console/v1/projects/wards/members")).StatusCode);
        // His CLI too: an admin of Wards, but not of the server (he can't make projects, or see the first one).
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/console/v1/projects/wards/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.PostAsJsonAsync("/console/v1/projects", new { name = "Mine", id = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/console/v1/projects/clinic/project")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/console/v1/projects/wards/members/{bobId}")).StatusCode);
        Assert.False((await Json(await bob.GetAsync("/console/v1/projects/wards/access"))).GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/console/v1/projects/wards/project")).StatusCode);   // nor can his CLI
    }

    [Fact]
    public async Task A_project_is_archived_then_restored_with_its_data_or_deleted_for_good()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var db = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(db, files, ("Winche:ProjectsPath", projectsPath));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/members", new { email = "bob@example.com", role = "Viewer" }));

        // No project is special: clinic (made first) can be archived too, and restored.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/console/v1/projects/clinic/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/console/v1/projects/clinic/restore", null)).StatusCode);

        // Archived: its addresses are refused, it's listed as archived, and its id is taken.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/console/v1/projects/wards/archive", null)).StatusCode);
        var gone = await admin.GetAsync("/console/v1/projects/wards/project");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains("project-archived", await gone.Content.ReadAsStringAsync());
        var listed = await Json(await admin.GetAsync("/console/v1/projects"));
        Assert.Equal(["clinic"], listed.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal("wards", listed.GetProperty("archived")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Again", id = "wards" })).StatusCode);
        // Only archived projects are deleted.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/console/v1/projects/clinic")).StatusCode);

        // Restored: back at its address, with its members.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/console/v1/projects/wards/restore", null)).StatusCode);
        Assert.Equal("bob@example.com", (await Json(await admin.GetAsync("/console/v1/projects/wards/members"))).GetProperty("members")[0].GetProperty("email").GetString());

        // Archived again and deleted: its database is dropped, its address unknown, its id free again.
        await admin.PostAsync("/console/v1/projects/wards/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/console/v1/projects/wards")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/console/v1/projects/wards/project")).StatusCode);
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", conn);
            cmd.Parameters.AddWithValue(server.Services.GetRequiredService<ProjectHosts>().DatabaseName("wards"));
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.False(Directory.Exists(Path.Combine(projectsPath, "wards")));
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards anew", id = "wards" }));
        Assert.Equal(0, (await Json(await admin.GetAsync("/console/v1/projects/wards/members"))).GetProperty("members").GetArrayLength());
    }

    private static async Task<HashSet<string>> TablesAsync(string connection, string schema)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = $1", conn);
        cmd.Parameters.AddWithValue(schema);
        var names = new HashSet<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public async Task Configuration_lives_in_the_control_database_and_data_in_each_projects_own()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));
        await StartProductAsync(admin, "/projects/wards", "authentication");
        await Json(await admin.PostAsJsonAsync("/projects/wards/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/integrity/apps", new { name = "Ward app", id = "ward-app" }));

        // The control database: accounts, keys and the project list in public; each project's configuration in its schema.
        var publicTables = await TablesAsync(control, "public");
        Assert.Contains("winche_console_invites", publicTables);
        Assert.DoesNotContain("AspNetUsers", publicTables);   // console accounts are a Winche Auth (console_accounts)
        Assert.Contains("winche_server_projects", publicTables);
        Assert.DoesNotContain("winche_settings", publicTables);
        Assert.Contains("winche_settings", await TablesAsync(control, "project_clinic"));   // clinic is a project like any other
        var wardsConfig = await TablesAsync(control, "project_wards");
        Assert.Contains("winche_settings", wardsConfig);
        Assert.Contains("console_rule_versions", wardsConfig);
        Assert.DoesNotContain("auth_users", wardsConfig);

        // Wards' data database: its users and data, none of its configuration.
        var wardsData = new NpgsqlConnectionStringBuilder(control) { Database = server.Services.GetRequiredService<ProjectHosts>().DatabaseName("wards") }.ConnectionString;
        var data = await TablesAsync(wardsData, "public");
        Assert.Contains("auth_users", data);
        Assert.DoesNotContain("winche_settings", data);
        Assert.DoesNotContain("winche_secrets", data);
        Assert.DoesNotContain("console_rule_versions", data);

        // Deleting a project takes its configuration too.
        await admin.PostAsync("/console/v1/projects/wards/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/console/v1/projects/wards")).StatusCode);
        Assert.Empty(await TablesAsync(control, "project_wards"));
    }

    [Fact]
    public async Task Server_settings_start_from_the_deployment_and_changes_in_the_console_win()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication");

        // What the deployment set is where they start.
        var page = await Json(await admin.GetAsync("/console/v1/server/settings"));
        Assert.Equal("deployment", page.GetProperty("source").GetString());
        Assert.Equal("http://localhost", page.GetProperty("deployment").GetProperty("publicUrl").GetString());
        Assert.False(page.GetProperty("settings").GetProperty("requireTwoFactorForSettings").GetBoolean());

        // The public address is the deployment's: the console can't change it (the issuer of every token).
        var settings = System.Text.Json.Nodes.JsonNode.Parse(page.GetProperty("settings").GetRawText())!.AsObject();
        settings["publicUrl"] = "https://api.example.com";
        var refused = await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings, reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Winche__PublicUrl", await refused.Content.ReadAsStringAsync());

        // Other settings are saved with a reason, and the console's value wins.
        settings["publicUrl"] = "http://localhost";
        settings["consolePasswordsOnlyForAdmins"] = true;
        settings["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "staff", ["enabled"] = true, ["issuer"] = "https://id.example.com/realms/staff", ["clientId"] = "winche-console",
        });
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings, reason = "Our web app" }));
        page = await Json(await admin.GetAsync("/console/v1/server/settings"));
        Assert.True(page.GetProperty("settings").GetProperty("consolePasswordsOnlyForAdmins").GetBoolean());
        Assert.Equal("console", page.GetProperty("source").GetString());
        Assert.Equal("Our web app", page.GetProperty("version").GetProperty("reason").GetString());
        Assert.Equal("http://localhost", page.GetProperty("settings").GetProperty("publicUrl").GetString());

        // Passwords are write-only.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/server/secrets/server.consoleSignIn.staff.clientSecret", new { value = "s3cret" })).StatusCode);
        var secret = (await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("secrets")[0];
        Assert.NotEqual(JsonValueKind.Null, secret.GetProperty("setAt").ValueKind);
        Assert.DoesNotContain("s3cret", await (await admin.GetAsync("/console/v1/server/settings")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/console/v1/server/secrets/anything", new { value = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_new_project_has_its_data_on_the_database_server_chosen_when_its_made()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");

        // "Another" server: the test's PostgreSQL by its address, user and password, as a separate database server would be.
        var target = new NpgsqlConnectionStringBuilder(control);
        var database = new { host = target.Host, port = target.Port, username = target.Username, password = target.Password, sslMode = "Disable", database = "wards_data" };

        // Checked first: a wrong password says why; the right one is ready, the database to be made.
        var wrong = await Json(await admin.PostAsJsonAsync("/console/v1/projects/check-database", new { id = "wards", database = database with { password = "nope" } }));
        Assert.False(wrong.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Couldn't connect", wrong.GetProperty("problem").GetString());
        var ready = await Json(await admin.PostAsJsonAsync("/console/v1/projects/check-database", new { id = "wards", database }));
        Assert.True(ready.GetProperty("ok").GetBoolean());
        Assert.False(ready.GetProperty("exists").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards", database = database with { password = "nope" } })).StatusCode);

        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards", database }));
        Assert.Equal("Wards", (await Json(await admin.GetAsync("/console/v1/projects/wards/project"))).GetProperty("name").GetString());
        // The project list says where its database is; its password is a secret of its own.
        await using var conn = new NpgsqlConnection(control);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT db_host, db_port, db_username, database FROM winche_server_projects WHERE id = 'wards'", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal((target.Host, target.Port, target.Username, "wards_data"), (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
        await r.DisposeAsync();

        // Without a database server: one of its own on the server's PostgreSQL, as before.
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Labs", id = "labs" }));
        await using var labs = new NpgsqlCommand("SELECT db_host FROM winche_server_projects WHERE id = 'labs'", conn);
        Assert.Equal(DBNull.Value, await labs.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_new_master_key_takes_over_with_the_old_one_as_previous_and_everything_moves_to_it()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var db = await pg.NewDatabaseAsync();
        const string oldKey = "old-master-key-0123456789abcdefghijklmnopq", newKey = "new-master-key-0123456789abcdefghijklmnopq";
        (string, string) Key(string k) => ("Winche:MasterKey", k);
        (string, string) TwoStepOff = ("Winche:RequireTwoFactorForSettings", "false");
        string cookie;
        await using (var before = new ServerFactory(db, files, Key(oldKey), TwoStepOff))
        {
            var admin = before.CreateClient(new() { HandleCookies = false });
            await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
            var login = await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" });
            cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
            admin.DefaultRequestHeaders.Add("Cookie", cookie);
            await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication");
            await AddProviderAsync(admin, new FakeOidcProvider());   // a server secret: its client secret
            await Json(await before.CreateClient().PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        }

        // The new key with the old one as previous: the console sign-in, the app's users and the secrets all carry on.
        await using (var rotating = new ServerFactory(db, files, Key(newKey), ("Winche:PreviousMasterKeys:0", oldKey), TwoStepOff))
        {
            var admin = rotating.CreateClient(new() { HandleCookies = false });
            admin.DefaultRequestHeaders.Add("Cookie", cookie);
            // Started with the old key as previous: every secret moved to the new one (nothing for the console to do).
            await Json(await admin.GetAsync("/console/v1/server/settings"));
            await Json(await rotating.CreateClient().PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        }

        // Then the new key alone: nothing needed the old one any more.
        await using var after = new ServerFactory(db, files, Key(newKey), TwoStepOff);
        var again = after.CreateClient(new() { HandleCookies = false });
        again.DefaultRequestHeaders.Add("Cookie", cookie);
        var final = await Json(await again.GetAsync("/console/v1/server/settings"));
        Assert.NotEqual(JsonValueKind.Null, final.GetProperty("secrets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "server.consoleSignIn.staff.clientSecret").GetProperty("setAt").ValueKind);
        await Json(await after.CreateClient().PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task A_request_body_that_cant_be_read_is_refused_with_the_reason()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication", "database");
        var app = server.CreateClient();
        foreach (var (path, body) in new[]
        {
            ("/projects/clinic/auth/v1/signUp", "{oops"),
            ("/projects/clinic/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), """{"fields":{"x":{"bogusValue":1}}}"""),
            ("/projects/clinic/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), "[1,2]"),
            ("/projects/clinic/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), "{}"),
        })
        {
            var resp = await app.SendAsync(new HttpRequestMessage(path.Contains("/documents/") ? HttpMethod.Put : HttpMethod.Post, path)
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
            Assert.Equal("invalid-argument", error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("Winche.", message);   // plain words, not .NET type names
            if (body == "{}") Assert.Contains("missing 'fields'", message);
        }
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task The_console_signs_people_in_through_an_identity_provider_with_roles_from_its_groups()
    {
        await using var idp = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");

        var signIn = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["scopes"] = "openid profile email", ["buttonLabel"] = "Sign in with Staff ID",
            ["roleClaim"] = "groups", ["adminValues"] = new System.Text.Json.Nodes.JsonArray("winche-admins"),
            ["createAccounts"] = true, ["trustProviderTwoStep"] = true, ["id"] = "staff",
        };
        idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true, ["given_name"] = "Bob", ["groups"] = new[] { "staff" } };

        // A test sign-in with the unsaved settings: what came back and the role it maps to; nobody signed in.
        var test = await Json(await admin.PostAsJsonAsync("/console/v1/server/console-sign-in/test", new { settings = signIn, clientSecret = idp.ClientSecret }));
        Assert.Equal("http://localhost/console/v1/session/oidc/callback", test.GetProperty("callbackUrl").GetString());
        var tester = server.CreateClient(new() { AllowAutoRedirect = false });
        var report = await FollowAsync(tester, idp, "/console/" + test.GetProperty("url").GetString());
        var html = await report.Content.ReadAsStringAsync();
        Assert.Contains("winche-oidc-test", html);
        Assert.Contains("\"role\":null", html);   // staff isn't an admin group: no server role
        Assert.Contains("bob@example.com", html);

        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(signIn);
        page["consolePasswordsOnlyForAdmins"] = true;
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Staff ID" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/server/secrets/server.consoleSignIn.staff.clientSecret", new { value = idp.ClientSecret })).StatusCode);

        // The sign-in page offers it.
        var anonymous = await Json(await server.CreateClient().GetAsync("/console/v1/session/state"));
        var offered = anonymous.GetProperty("oidc").GetProperty("providers").EnumerateArray().Single();
        Assert.Equal(("staff", "Sign in with Staff ID"), (offered.GetProperty("id").GetString(), offered.GetProperty("label").GetString()));

        // Bob signs in: a console account is made for him (not a server admin: his group isn't one), two-step the provider's.
        var bob = server.CreateClient(new() { AllowAutoRedirect = false });
        var landed = await FollowAsync(bob, idp, "/console/v1/session/oidc/start?provider=staff&returnUrl=/console/projects/clinic/");
        Assert.Equal("/console/projects/clinic/", landed.Headers.Location!.OriginalString);
        var state = (await Json(await bob.GetAsync("/console/v1/session/state"))).GetProperty("user");
        Assert.Equal(("bob@example.com", JsonValueKind.Null, true), (state.GetProperty("email").GetString(), state.GetProperty("role").ValueKind, state.GetProperty("signedInWithProvider").GetBoolean()));
        // A project is its members': none of clinic's until an admin adds him, then at once.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/console/v1/projects/clinic/project")).StatusCode);
        await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/members", new { email = "bob@example.com", role = "Member" }));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/console/v1/projects/clinic/project")).StatusCode);

        // His groups change at the provider: the next sign-in brings it (here only in the access token, as Keycloak does).
        idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true };
        idp.NextAccess = new() { ["sub"] = "u-1", ["groups"] = new[] { "staff", "winche-admins" } };
        var bob2 = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(bob2, idp, "/console/v1/session/oidc/start");
        Assert.True((await Json(await bob2.GetAsync("/console/v1/session/state"))).GetProperty("user").GetProperty("serverAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bob2.GetAsync("/console/v1/server/settings")).StatusCode);   // admin now, at once

        // Passwords are for server admins only now; the built-in admin still gets in (the way in when the provider is down).
        await Json(await server.CreateClient().PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));

        // An address already taken that the provider doesn't vouch for isn't linked.
        idp.NextAccess = null;
        idp.Next = new() { ["sub"] = "u-2", ["email"] = "admin@example.com", ["email_verified"] = false };
        var mallory = server.CreateClient(new() { AllowAutoRedirect = false });
        var refused = await FollowAsync(mallory, idp, "/console/v1/session/oidc/start");
        Assert.Contains("signInError=", refused.Headers.Location!.OriginalString);
        Assert.Equal(JsonValueKind.Null, (await Json(await mallory.GetAsync("/console/v1/session/state"))).GetProperty("user").ValueKind);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task An_identity_provider_never_links_by_email_to_an_account_in_use_and_the_owner_adds_or_removes_it_themselves()
    {
        await using var idp = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient(new() { AllowAutoRedirect = false });
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["scopes"] = "openid profile email", ["buttonLabel"] = "Staff ID",
            ["createAccounts"] = true, ["id"] = "staff",
        });
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Staff ID" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/server/secrets/server.consoleSignIn.staff.clientSecret", new { value = idp.ClientSecret })).StatusCode);
        async Task<bool> SignsInAs(string sub, string email)
        {
            idp.Next = new() { ["sub"] = sub, ["email"] = email, ["email_verified"] = true };
            var client = server.CreateClient(new() { AllowAutoRedirect = false });
            var landed = await FollowAsync(client, idp, "/console/v1/session/oidc/start?provider=staff");
            var user = (await Json(await client.GetAsync("/console/v1/session/state"))).GetProperty("user");
            return !landed.Headers.Location!.OriginalString.Contains("signInError=") && user.ValueKind != JsonValueKind.Null && user.GetProperty("email").GetString() == email;
        }

        // The admin's account is in use: a provider account with the same (verified) address doesn't get in.
        Assert.False(await SignsInAs("intruder", "admin@example.com"));

        // Someone with no console account yet: the provider makes one for them (it may: createAccounts).
        Assert.True(await SignsInAs("dora-1", "dora@example.com"));

        // The admin adds the provider themselves, signed in: from then on it signs them in.
        var methods = await Json(await admin.GetAsync("/console/v1/session/sign-in-methods"));
        Assert.False(methods.GetProperty("providers").EnumerateArray().Single().GetProperty("linked").GetBoolean());
        idp.Next = new() { ["sub"] = "admin-1", ["email"] = "admin@example.com", ["email_verified"] = true };
        var linked = await FollowAsync(admin, idp, "/console/v1/session/oidc/start?provider=staff&link=true&returnUrl=/console/");
        Assert.DoesNotContain("signInError=", linked.Headers.Location!.OriginalString);
        Assert.True((await Json(await admin.GetAsync("/console/v1/session/sign-in-methods"))).GetProperty("providers").EnumerateArray().Single().GetProperty("linked").GetBoolean());
        Assert.Equal("admin@example.com", (await Json(await admin.GetAsync("/console/v1/session/state"))).GetProperty("user").GetProperty("email").GetString());   // still their session
        Assert.True(await SignsInAs("admin-1", "admin@example.com"));

        // …and removes it: it's gone for good, not linked again by its address.
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/console/v1/session/sign-in-methods/staff")).StatusCode);
        Assert.False(await SignsInAs("admin-1", "admin@example.com"));

        // Adding needs a signed-in account.
        var nobody = server.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Contains("signInError=", (await nobody.GetAsync("/console/v1/session/oidc/start?provider=staff&link=true")).Headers.Location!.OriginalString);
        Directory.Delete(files, recursive: true);
    }

    /// <summary>Starts a product in a project's console and waits for its setup to finish.</summary>
    internal static async Task StartProductAsync(HttpClient console, string prefix, string product)
    {
        await Json(await console.PostAsync($"/console/v1{prefix}/project/products/{product}/start", null));
        for (var i = 0; i < 100; i++)
        {
            var run = await Json(await console.GetAsync($"/console/v1{prefix}/project/products/{product}/setup"));
            var state = run.GetProperty("state").GetString();
            if (state == "done") return;
            if (state == "failed") throw new InvalidOperationException($"Setting up {product} failed: {run}");
            await Task.Delay(100);
        }
        throw new TimeoutException($"Setting up {product} didn't finish.");
    }

    [Fact]
    public async Task A_fresh_server_is_claimed_only_with_the_setup_code_from_its_log()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        // No code from the deployment: the server makes one, keeps it, and logs it.
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:SetupCode", ""));
        var console = server.CreateClient();
        var state = await Json(await console.GetAsync("/console/v1/session/state"));
        Assert.True(state.GetProperty("setup").GetProperty("code").GetBoolean());

        var account = new { email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" };
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/console/v1/session/setup", account)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/console/v1/session/setup/code", new { setupCode = "AAAA-BBBB-CCCC" })).StatusCode);

        // The code the server made (and logged): accepted however it's typed.
        var code = await server.Services.GetRequiredService<ServerSettingsProvider>().SetupCodeAsync(CancellationToken.None);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal(HttpStatusCode.NoContent, (await console.PostAsJsonAsync("/console/v1/session/setup/code", new { setupCode = code.ToLowerInvariant().Replace("-", " ") })).StatusCode);
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = code, account.email, account.firstName, account.lastName, account.password }));

        // Set up: no more setup, and the code isn't asked for again.
        state = await Json(await console.GetAsync("/console/v1/session/state"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("setup").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await console.PostAsJsonAsync("/console/v1/session/setup/code", new { setupCode = code })).StatusCode);
    }

    /// <summary>A provider in Server settings, its secret saved: "staff", at <paramref name="idp"/>.</summary>
    private static async Task AddProviderAsync(HttpClient admin, FakeOidcProvider idp, string id = "staff", string label = "Sign in with Staff ID")
    {
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        var list = page["consoleProviders"]?.AsArray() ?? [];
        list.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id, ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["buttonLabel"] = label, ["createAccounts"] = true,
        });
        page["consoleProviders"] = list.DeepClone();
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Add " + id }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/console/v1/server/secrets/server.consoleSignIn.{id}.clientSecret", new { value = idp.ClientSecret })).StatusCode);
    }

    private static async Task<HttpClient> AdminAsync(ServerFactory server)
    {
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        return admin;
    }

    private static async Task<string?> SignedInAsync(HttpClient client) =>
        (await Json(await client.GetAsync("/console/v1/session/state"))).GetProperty("user") is { ValueKind: JsonValueKind.Object } u ? u.GetProperty("email").GetString() : null;

    [Fact]
    public async Task The_console_offers_several_identity_providers_and_one_person_adds_each_to_the_same_account()
    {
        await using var staff = new FakeOidcProvider();
        await using var partner = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await AddProviderAsync(admin, staff);
        await AddProviderAsync(admin, partner, "partner", "Sign in with Partner ID");

        var offered = (await Json(await server.CreateClient().GetAsync("/console/v1/session/state"))).GetProperty("oidc").GetProperty("providers")
            .EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(["staff", "partner"], offered);
        Assert.Equal(HttpStatusCode.NotFound, (await server.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/console/v1/session/oidc/start")).StatusCode);   // which one?

        staff.Next = new() { ["sub"] = "s-1", ["email"] = "bob@example.com", ["email_verified"] = true };
        partner.Next = new() { ["sub"] = "p-9", ["email"] = "bob@example.com", ["email_verified"] = true };
        var viaStaff = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(viaStaff, staff, "/console/v1/session/oidc/start?provider=staff");
        // His account is in use now: Partner ID with the same address isn't linked to it by itself…
        var early = server.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Contains("signInError=", (await FollowAsync(early, partner, "/console/v1/session/oidc/start?provider=partner")).Headers.Location!.OriginalString);
        // …he adds it, signed in, from his account menu; then it signs him in to the same account.
        partner.Next = new() { ["sub"] = "p-9", ["email"] = "bob@example.com", ["email_verified"] = true };
        await FollowAsync(viaStaff, partner, "/console/v1/session/oidc/start?provider=partner&link=true&returnUrl=/console/");
        partner.Next = new() { ["sub"] = "p-9", ["email"] = "bob@example.com", ["email_verified"] = true };
        var viaPartner = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(viaPartner, partner, "/console/v1/session/oidc/start?provider=partner");
        var a = (await Json(await viaStaff.GetAsync("/console/v1/session/state"))).GetProperty("user").GetProperty("id").GetString();
        var b = (await Json(await viaPartner.GetAsync("/console/v1/session/state"))).GetProperty("user").GetProperty("id").GetString();
        Assert.Equal(a, b);

        // A provider taken off the list: its sessions end, and its secret goes with it.
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(page["consoleProviders"]!.AsArray().First(p => (string?)p!["id"] == "staff")!.DeepClone());
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "No more partners" }));
        Assert.Null(await SignedInAsync(viaPartner));
        Assert.Equal("bob@example.com", await SignedInAsync(viaStaff));
        var secrets = (await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("secrets").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("server.consoleSignIn.partner.clientSecret", secrets);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task A_console_session_from_a_provider_lasts_as_long_as_the_providers_does()
    {
        await using var idp = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await AddProviderAsync(admin, idp);
        var every = Winche.Console.Identity.ConsoleOidc.CheckEvery;
        Winche.Console.Identity.ConsoleOidc.CheckEvery = TimeSpan.Zero;   // checked with the provider at every request
        try
        {
            // Renewed with the provider while it lasts there.
            idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true };
            var bob = server.CreateClient(new() { AllowAutoRedirect = false });
            await FollowAsync(bob, idp, "/console/v1/session/oidc/start");
            Assert.Equal("bob@example.com", await SignedInAsync(bob));
            Assert.Equal("bob@example.com", await SignedInAsync(bob));
            Assert.True(idp.Refreshes >= 2);

            // Signed out at the provider: the provider won't renew it, and the console's session ends.
            idp.SignOut("u-1");
            Assert.Null(await SignedInAsync(bob));

            // Back-channel logout of his session: it ends at once; a session from another sign-in stays.
            var again = server.CreateClient(new() { AllowAutoRedirect = false });
            await FollowAsync(again, idp, "/console/v1/session/oidc/start");
            var other = server.CreateClient(new() { AllowAutoRedirect = false });
            await FollowAsync(other, idp, "/console/v1/session/oidc/start");
            Assert.Equal(HttpStatusCode.BadRequest, (await server.CreateClient().PostAsync("/console/v1/session/oidc/backchannel-logout",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = "not-a-token" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().PostAsync("/console/v1/session/oidc/backchannel-logout",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = idp.LogoutToken(idp.LastSid, null) }))).StatusCode);
            Assert.Null(await SignedInAsync(other));
            Assert.Equal("bob@example.com", await SignedInAsync(again));
            // All of his sessions (by subject).
            Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().PostAsync("/console/v1/session/oidc/backchannel-logout",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = idp.LogoutToken(null, "u-1") }))).StatusCode);
            Assert.Null(await SignedInAsync(again));

            // A password sign-in isn't touched by any of it.
            Assert.Equal("admin@example.com", await SignedInAsync(admin));
        }
        finally { Winche.Console.Identity.ConsoleOidc.CheckEvery = every; }
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Server_settings_authentication_is_the_project_authentication_api_over_the_console_accounts()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication");
        await Json(await server.CreateClient().PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "patient@example.com", password = "correct horse battery" }));

        // The console's accounts, not the app's users.
        var users = await Json(await admin.GetAsync("/console/v1/server/auth/users"));
        Assert.Equal(["admin@example.com"], users.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!));
        var appUsers = await Json(await admin.GetAsync("/console/v1/projects/clinic/auth/users"));
        Assert.Equal(["patient@example.com"], appUsers.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!));

        // Its configuration, templates and sign-in settings are the accounts' own.
        Assert.True((await Json(await admin.GetAsync("/console/v1/server/auth/config"))).GetProperty("wincheAuth").GetBoolean());
        Assert.True((await Json(await admin.GetAsync("/console/v1/server/auth/email-templates"))).GetProperty("templates").GetArrayLength() > 0);
        var signin = await Json(await admin.GetAsync("/console/v1/server/auth/settings/signin"));
        Assert.Equal(8, signin.GetProperty("settings").GetProperty("emailPassword").GetProperty("minLength").GetInt32());   // the console's minimum

        // Only server admins.
        await Json(await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "member@example.com", role = "none", password = "Passw0rd!long" }));   // not a server admin
        var member = server.CreateClient();
        await Json(await member.PostAsJsonAsync("/console/v1/session/login", new { email = "member@example.com", password = "Passw0rd!long" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/console/v1/server/auth/users")).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task The_consoles_identity_providers_are_the_console_accounts_sign_in_providers_with_their_keycloak_sync()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "staff", ["enabled"] = true, ["issuer"] = "https://id.example.com/realms/staff", ["clientId"] = "winche-console",
            ["buttonLabel"] = "Sign in with Staff ID", ["trustProviderTwoStep"] = true,
            ["keycloakSync"] = new System.Text.Json.Nodes.JsonObject { ["clientId"] = "winche-sync" },
        });
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Staff" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/server/secrets/server.consoleSignIn.staff.keycloakSync.clientSecret", new { value = "sync-secret" })).StatusCode);

        var accounts = server.Services.GetRequiredService<ConsoleAccounts>();
        var area = accounts.Services.GetRequiredService<Winche.Core.SettingsArea<Winche.Auth.Settings.WincheAuthSettings>>();
        Winche.Auth.Settings.SignInProviderSettings? staff = null;
        for (var i = 0; i < 50 && (staff = area.Current.Providers.FirstOrDefault(p => p.Name == "staff")) is null; i++) await Task.Delay(100);
        Assert.NotNull(staff);
        Assert.Equal(("https://id.example.com/realms/staff", "winche-console", true, "winche-sync"),
            (staff!.Authority, staff.Audiences.Single(), staff.CountsAsSecondStep, staff.KeycloakSync?.ClientId));
        string? copied = null;
        for (var i = 0; i < 50 && copied is null; i++)
        {
            copied = await accounts.Services.GetRequiredService<Winche.Secrets.SecretStore>().GetAsync(Winche.Auth.Settings.KeycloakSyncSettings.SecretName("staff"));
            if (copied is null) await Task.Delay(100);
        }
        Assert.Equal("sync-secret", copied);

        // A save the server reads as keeping the sync keeps its secret, however the document spells it (the same key
        // twice, in two cases: read as the save reads it, the last one).
        async Task<bool> SyncSecretKept() => (await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("secrets").EnumerateArray()
            .Any(x => x.GetProperty("name").GetString() == "server.consoleSignIn.staff.keycloakSync.clientSecret" && x.GetProperty("setAt").ValueKind != JsonValueKind.Null);
        Assert.True(await SyncSecretKept());
        foreach (var objectFirst in new[] { true, false })
        {
            // Back to a provider with a sync, and its secret.
            var plain = page["consoleProviders"]![0]!.AsObject();
            plain.Remove("KeycloakSync");
            plain["keycloakSync"] = new System.Text.Json.Nodes.JsonObject { ["clientId"] = "winche-sync" };
            await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Sync" }));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/server/secrets/server.consoleSignIn.staff.keycloakSync.clientSecret", new { value = "sync-secret" })).StatusCode);

            var twice = page["consoleProviders"]![0]!.AsObject();
            twice.Remove("keycloakSync");
            var sync = new System.Text.Json.Nodes.JsonObject { ["clientId"] = "winche-sync" };
            if (objectFirst) { twice["keycloakSync"] = sync; twice["KeycloakSync"] = null; }
            else { twice["keycloakSync"] = null; twice["KeycloakSync"] = sync; }
            await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "Spelt twice" }));
            var saved = (await Json(await admin.GetAsync("/console/v1/server/settings"))).GetProperty("settings").GetProperty("consoleProviders")[0];
            var synced = saved.TryGetProperty("keycloakSync", out var k) && k.ValueKind == JsonValueKind.Object;
            Assert.Equal(synced, await SyncSecretKept());   // the secret goes exactly when the saved settings drop the sync
            twice.Remove("KeycloakSync");
        }

        // Taken off Server settings: off the accounts' providers too (its sync stops).
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray();
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = page, reason = "No more staff" }));
        for (var i = 0; i < 50 && area.Current.Providers.Any(p => p.Name == "staff"); i++) await Task.Delay(100);
        Assert.DoesNotContain(area.Current.Providers, p => p.Name == "staff");
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task The_one_console_provider_of_before_moves_into_the_list_with_its_secret()
    {
        await using var idp = new FakeOidcProvider();
        var db = await pg.NewDatabaseAsync();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using (var before = new ServerFactory(db, files, ("Winche:RequireTwoFactorForSettings", "false")))
        {
            var admin = await AdminAsync(before);
            // The shape of before: one provider, its secret under the one name, passwords-only-for-admins on it.
            var settings = before.Services.GetRequiredService<ServerSettingsProvider>();
            var doc = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(settings.Current, Winche.Core.SettingsArea.Json))!.AsObject();
            doc["consoleSignIn"] = new System.Text.Json.Nodes.JsonObject
            {
                ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["buttonLabel"] = "Sign in with Staff ID", ["passwordsOnlyForAdmins"] = true,
            };
            await settings.Store.SaveAsync(ServerSettings.Area, doc, "test", "the old shape", CancellationToken.None);
            await settings.Secrets.SetAsync(ServerSettings.LegacyConsoleSignInSecret, idp.ClientSecret, "test", CancellationToken.None);
        }
        await using var after = new ServerFactory(db, files, ("Winche:RequireTwoFactorForSettings", "false"));
        var now = after.Services.GetRequiredService<ServerSettingsProvider>();
        var provider = Assert.Single(now.Current.ConsoleProviders);
        Assert.Equal(("default", idp.Issuer, true, true), (provider.Id, provider.Issuer, provider.Enabled, now.Current.ConsolePasswordsOnlyForAdmins));
        Assert.Null(now.Current.LegacyConsoleSignIn);
        Assert.Equal(idp.ClientSecret, await now.ConsoleProviderSecretAsync("default", CancellationToken.None));
        Assert.Null(await now.Secrets.GetAsync(ServerSettings.LegacyConsoleSignInSecret, CancellationToken.None));

        // And it signs people in.
        idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true };
        var bob = after.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(bob, idp, "/console/v1/session/oidc/start");
        Assert.Equal("bob@example.com", await SignedInAsync(bob));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task A_project_with_nothing_started_answers_its_overview_auth_config_and_keys_without_reading_missing_tables()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");

        var overview = await Json(await admin.GetAsync("/console/v1/projects/clinic/overview"));
        Assert.All(overview.GetProperty("products").EnumerateArray(), p => Assert.False(p.GetProperty("on").GetBoolean()));
        var config = await Json(await admin.GetAsync("/console/v1/projects/clinic/auth/config"));
        Assert.Equal(0, config.GetProperty("keys").GetArrayLength());
        // No signing keys yet: refused like the rest of Authentication's API, not a server error.
        var jwks = await server.CreateClient().GetAsync("/projects/clinic/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.BadRequest, jwks.StatusCode);
        Assert.Contains("service-disabled", await jwks.Content.ReadAsStringAsync());

        // Started: its keys are there.
        await StartProductAsync(admin, "/projects/clinic", "authentication");
        Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().GetAsync("/projects/clinic/.well-known/jwks.json")).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Console_accounts_are_a_winche_auth_of_their_own_apart_from_every_projects_app_users()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var db = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(db, files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication");

        // An account in the console's Winche Auth signs in with its password, and gets tokens from the console's issuer.
        var accounts = server.Services.GetRequiredService<ConsoleAccounts>();
        await using (var scope = accounts.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<Winche.Auth.IWincheAuth>();
            var ann = await auth.CreateUserAsync("ann@clinic.example", "correct horse battery", "Ann");
            var tokens = await auth.SignInAsync("ann@clinic.example", "correct horse battery", Winche.Auth.AuthRequestInfo.Server);
            Assert.Equal(ann.Uid, tokens.User.Uid);
            Assert.Equal("http://localhost/console/accounts", new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(tokens.AccessToken).Issuer);
            Assert.Equal(ann.Uid, (await auth.FindByEmailAsync("ann@clinic.example"))!.Uid);
        }

        // The project's app users are another Winche Auth: Ann isn't one of them, and can't sign in to the app.
        var refused = await server.CreateClient().PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@clinic.example", password = "correct horse battery" });
        Assert.False(refused.IsSuccessStatusCode);
        // Its tables are in their own schema of the control database.
        await using var conn = new NpgsqlConnection(db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM console_accounts.auth_users", conn);
        Assert.Equal(2L, await cmd.ExecuteScalarAsync());   // Ann, and the admin who set the server up
        Directory.Delete(files, recursive: true);
    }

    /// <summary>Follows a sign-in: the console's redirects in the test server, the provider's over the network.</summary>
    private static async Task<HttpResponseMessage> FollowAsync(HttpClient console, FakeOidcProvider idp, string path)
    {
        using var network = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await console.GetAsync(path);
        for (var i = 0; i < 5 && response.StatusCode == HttpStatusCode.Redirect; i++)
        {
            var next = response.Headers.Location!;
            if (next.IsAbsoluteUri && next.OriginalString.StartsWith(idp.Issuer)) response = await network.GetAsync(next);
            else if (next.IsAbsoluteUri && next.Host == "localhost") response = await console.GetAsync(next.PathAndQuery);
            else return response;   // back in the console
        }
        return response;
    }

    [Fact]
    public async Task A_product_makes_its_tables_only_once_started_and_its_setup_steps_can_be_followed()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));

        var data = new NpgsqlConnectionStringBuilder(control) { Database = server.Services.GetRequiredService<ProjectHosts>().DatabaseName("wards") }.ConnectionString;
        async Task<List<string>> Tables()
        {
            await using var conn = new NpgsqlConnection(data);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name NOT IN ('console_logs', 'console_log_queries', 'console_log_settings', 'winche_schema') ORDER BY 1", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<string>();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }
        Assert.Empty(await Tables());   // nothing started, nothing made

        // Starting the database runs its setup in the background; its steps can be followed, then it answers.
        var started = await admin.PostAsync("/console/v1/projects/wards/project/products/database/start", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = await Json(started);
        Assert.Contains(run.GetProperty("steps").EnumerateArray(), s => s.GetProperty("title").GetString() == "Create the document tables and functions");
        await StartProductAsync(admin, "/projects/wards", "database");
        var tables = await Tables();
        Assert.Contains("winche_documents", tables);
        Assert.Contains("winche_hook_deliveries", tables);
        Assert.DoesNotContain("auth_users", tables);   // Auth isn't started
        Assert.DoesNotContain("winche_files", tables);
        var project = await Json(await admin.GetAsync("/console/v1/projects/wards/project"));
        var database = project.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "database");
        Assert.True(database.GetProperty("ready").GetBoolean());
        Assert.DoesNotContain(project.GetProperty("products").EnumerateArray(), p => p.GetProperty("id").GetString() == "hooks");
        Assert.Equal(HttpStatusCode.NotFound, (await server.CreateClient().GetAsync("/projects/wards/documents/v1/notes/a")).StatusCode);   // answered by the database itself
    }

    [Fact]
    public async Task A_project_gets_a_database_of_its_own_and_moves_with_everything_kept()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        // Never the server's own database.
        var refused = await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Clinic", id = "clinic",
            database = new { database = new NpgsqlConnectionStringBuilder(control).Database } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, (await Json(await admin.GetAsync("/console/v1/projects"))).GetProperty("projects").GetArrayLength());

        // None named: a database of its own on this server's PostgreSQL.
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "authentication", "database");
        var app = server.CreateClient();
        var token = (await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }))).GetProperty("accessToken").GetString()!;
        var where = await Json(await admin.GetAsync("/console/v1/projects/clinic/settings/location/database"));
        var ownDb = where.GetProperty("database").GetString()!;
        Assert.EndsWith("_p_clinic", ownDb);
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(control) { Database = ownDb }.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM auth_users", c);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        // Nothing of the project in the server's database.
        await using (var c = new NpgsqlConnection(control))
        {
            await c.OpenAsync();
            var clean = false;
            for (var i = 0; i < 100 && !clean; i++)
            {
                await using var cmd = new NpgsqlCommand("SELECT to_regclass('public.auth_users') IS NULL AND to_regclass('public.console_logs') IS NULL", c);
                clean = (bool)(await cmd.ExecuteScalarAsync())!;
                if (!clean) await Task.Delay(100);
            }
            Assert.True(clean);
        }

        var newDb = where.GetProperty("suggestedDatabase").GetString()!;

        // A database already holding tables is refused; a new one on this server is fine.
        var check = await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/settings/location/database/check", new { database = new NpgsqlConnectionStringBuilder(control).Database }));
        Assert.False(check.GetProperty("ok").GetBoolean());
        check = await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/settings/location/database/check", new { database = newDb }));
        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/console/v1/projects/clinic/settings/location/database/move", new { database = newDb })).StatusCode);
        var sawPause = false;
        JsonElement after = default;
        // A minute at most, by the clock: a busy CI machine copies slowly.
        for (var until = DateTime.UtcNow.AddMinutes(1); DateTime.UtcNow < until;)
        {
            var r = await admin.GetAsync("/console/v1/projects/clinic/settings/location/database");
            if (r.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                Assert.Contains("project-moving", await r.Content.ReadAsStringAsync());
                sawPause = true;
            }
            else if (r.IsSuccessStatusCode && (after = await Json(r)).GetProperty("previous").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }
        Assert.True(after.ValueKind == JsonValueKind.Object && after.GetProperty("previous").ValueKind != JsonValueKind.Null, "The move didn't finish within a minute.");
        Assert.True(sawPause);
        Assert.Equal(newDb, after.GetProperty("database").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastMoveFailed").ValueKind);

        // Everything carries on there: the user signs in, their earlier token still works for the API.
        await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(control) { Database = newDb }.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM auth_users", c);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // Finishing deletes the old database.
        await admin.PostAsJsonAsync("/console/v1/projects/clinic/settings/location/database/finish", new { deleteOld = true });
        await using (var c = new NpgsqlConnection(control))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", c);
            cmd.Parameters.AddWithValue(ownDb);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.Equal(JsonValueKind.Null, (await Json(await admin.GetAsync("/console/v1/projects/clinic/settings/location/database"))).GetProperty("previous").ValueKind);
        await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        Assert.NotNull(token);
    }

    [Fact]
    public async Task A_projects_data_moves_to_another_server_and_the_old_database_goes_when_the_move_is_finished()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" }));
        await StartProductAsync(admin, "/projects/wards", "authentication");
        await Json(await server.CreateClient().PostAsJsonAsync("/projects/wards/auth/v1/signUp", new { email = "bob@example.com", password = "correct horse battery" }));
        var oldDb = (await Json(await admin.GetAsync("/console/v1/projects/wards/settings/location/database"))).GetProperty("database").GetString()!;

        // "Another server": the test's PostgreSQL by address, user and password.
        var other = new NpgsqlConnectionStringBuilder(control);
        var target = new { database = "wards_elsewhere", host = other.Host, port = other.Port, username = other.Username, password = other.Password, sslMode = "Disable" };
        Assert.True((await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/settings/location/database/check", target))).GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/console/v1/projects/wards/settings/location/database/move", target)).StatusCode);
        JsonElement after = default;
        // A minute at most, by the clock: a busy CI machine copies slowly.
        for (var until = DateTime.UtcNow.AddMinutes(1); DateTime.UtcNow < until;)
        {
            var r = await admin.GetAsync("/console/v1/projects/wards/settings/location/database");
            if (r.IsSuccessStatusCode && (after = await Json(r)).GetProperty("previous").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }
        Assert.True(after.ValueKind == JsonValueKind.Object && after.GetProperty("previous").ValueKind != JsonValueKind.Null, "The move didn't finish within a minute.");
        Assert.Equal(("wards_elsewhere", other.Host), (after.GetProperty("database").GetString(), after.GetProperty("host").GetString()));
        await Json(await server.CreateClient().PostAsJsonAsync("/projects/wards/auth/v1/signIn", new { email = "bob@example.com", password = "correct horse battery" }));

        // Finished: the old database is dropped.
        await admin.PostAsJsonAsync("/console/v1/projects/wards/settings/location/database/finish", new { deleteOld = true });
        await using (var c = new NpgsqlConnection(control))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", c) { Parameters = { new() { Value = oldDb } } };
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // Pool settings: saved, the project restarted with them, still answering.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/projects/wards/settings/location/database/pool", new { maxConnections = 20, commandTimeoutSeconds = 60 })).StatusCode);
        Assert.Equal(20, (await Json(await admin.GetAsync("/console/v1/projects/wards/settings/location/database"))).GetProperty("maxConnections").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/console/v1/projects/wards/settings/location/database/pool", new { maxConnections = 1 })).StatusCode);
        await Task.Delay(300);
        await Json(await server.CreateClient().PostAsJsonAsync("/projects/wards/auth/v1/signIn", new { email = "bob@example.com", password = "correct horse battery" }));
    }

    [Fact]
    public async Task Files_are_kept_on_disk_behind_signed_links_on_the_public_address()
    {
        // One folder for every project's files: the first project's too, in its own folder under it.
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), null, ("Winche:ProjectsPath", files));
        var app = server.CreateClient();
        var console = server.CreateClient();
        await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "database", "storage", "authentication", "functions", "hooks");
        var parsed = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/text/parse", new { text = "match public/{file=**} { allow read, write: if request.auth != null; }" }));
        await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/storage", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Public files" }));

        var session = await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ben@example.com", password = "correct horse battery" }));
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var path = B64("public/hello.txt");
        await Json(await app.PutAsJsonAsync($"/projects/clinic/files/v1/{path}", new { mimeType = "text/plain", sizeBytes = 5 }));
        var upload = (await Json(await app.PostAsync($"/projects/clinic/files/v1/{path}:upload", null))).GetProperty("url").GetString()!;
        Assert.StartsWith("http://localhost/projects/clinic/files/v1/signed/", upload);
        var put = new HttpRequestMessage(HttpMethod.Put, new Uri(upload).PathAndQuery) { Content = new StringContent("hello") };
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(put)).StatusCode);
        await Json(await app.PostAsync($"/projects/clinic/files/v1/{path}:confirm", null));
        // On disk in the project's folder, under its own key: the folder, a generation, the name.
        var stored = Assert.Single(Directory.GetFiles(files, "hello.txt", SearchOption.AllDirectories));
        Assert.Equal("hello", File.ReadAllText(stored));
        Assert.Matches($"^{Regex.Escape(files)}/[^/]+/files/objects/public/", stored);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Assets_are_published_from_the_console_and_read_by_apps_through_their_rules()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), null, ("Winche:ProjectsPath", files), ("Winche:RequireTwoFactorForSettings", "false"));
        var app = server.CreateClient(new() { AllowAutoRedirect = false });
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "database");

        // Not started: apps are told so, and the console reads nothing.
        var off = await app.GetAsync("/projects/clinic/assets/v1/channels/production");
        Assert.Contains("service-disabled", await off.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await console.GetAsync("/console/v1/projects/clinic/assets")).StatusCode);
        await StartProductAsync(console, "/projects/clinic", "assets");

        // Contents first (by hash, once), then the release, which moves the channel.
        var en = "{\"hello\":\"Hello\"}"u8.ToArray();
        var guide = "<p>staff only</p>"u8.ToArray();
        string Hash(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
        var missing = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/assets/contents/missing", new { hashes = new[] { Hash(en), Hash(guide) } }));
        Assert.Equal(2, missing.GetProperty("missing").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PutAsync($"/console/v1/projects/clinic/assets/contents/{Hash(en)}", new ByteArrayContent(en[..5]))).StatusCode);
        foreach (var b in new[] { en, guide })
            Assert.Equal(HttpStatusCode.NoContent, (await console.PutAsync($"/console/v1/projects/clinic/assets/contents/{Hash(b)}", new ByteArrayContent(b))).StatusCode);
        var noNote = await console.PostAsJsonAsync("/console/v1/projects/clinic/assets/releases", new { files = new { }, channel = "production" });
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        var release = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/assets/releases", new
        {
            note = "First translations",
            channel = "production",
            files = new Dictionary<string, object>
            {
                ["translations/en.json"] = new { hash = Hash(en), size = en.Length, mimeType = "" },
                ["staff/guide.html"] = new { hash = Hash(guide), size = guide.Length, mimeType = "" },
            },
        }));
        Assert.Equal(1, release.GetProperty("number").GetInt32());

        // Rules start as deny-all; published, they decide what apps list and read.
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync("/projects/clinic/assets/v1/channels/beta")).StatusCode);
        Assert.Equal(0, (await Json(await app.GetAsync("/projects/clinic/assets/v1/channels/production"))).GetProperty("files").GetArrayLength());
        var parsed = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/text/parse", new { text = "match translations/{rest=**} { allow read; }" }));
        await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/assets", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Translations are public" }));
        var listing = await Json(await app.GetAsync("/projects/clinic/assets/v1/channels/production"));
        Assert.Equal("translations/en.json", Assert.Single(listing.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/projects/clinic/assets/v1/channels/production/files/staff/guide.html")).StatusCode);

        // A file: a signed link into the project's files folder.
        var file = await app.GetAsync("/projects/clinic/assets/v1/channels/production/files/translations/en.json");
        Assert.Equal(HttpStatusCode.Redirect, file.StatusCode);
        Assert.StartsWith("http://localhost/projects/clinic/files/v1/signed/", file.Headers.Location!.ToString());
        var bytes = await app.GetByteArrayAsync(file.Headers.Location.PathAndQuery);
        Assert.Equal(en, bytes);
        Assert.Single(Directory.GetFiles(files, Hash(en), SearchOption.AllDirectories));

        // The console's view, and the overview's card.
        var page = await Json(await console.GetAsync("/console/v1/projects/clinic/assets"));
        Assert.Equal(1, Assert.Single(page.GetProperty("channels").EnumerateArray()).GetProperty("release").GetInt32());
        var overview = await Json(await console.GetAsync("/console/v1/projects/clinic/overview"));
        Assert.Contains(overview.GetProperty("products").EnumerateArray(), p => p.GetProperty("id").GetString() == "assets" && p.GetProperty("on").GetBoolean());
        Assert.True((await Json(await console.GetAsync("/console/v1/projects/clinic/access"))).GetProperty("capabilities").GetProperty("assets").GetBoolean());

        // CI publishes as a service account scoped to assets: it reads releases to compare, and publishes; it can't move contents.
        var account = (await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/service-accounts", new { name = "ci", scopes = new[] { "assets" } }))).GetProperty("id").GetString();
        var key = (await Json(await console.PostAsJsonAsync($"/console/v1/projects/clinic/service-accounts/{account}/keys", new { days = 7 }))).GetProperty("secret").GetString();
        var ci = server.CreateClient();
        var token = (await Json(await ci.PostAsJsonAsync("/console/v1/projects/clinic/service-accounts/token", new { key }))).GetProperty("accessToken").GetString();
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await Json(await ci.GetAsync("/console/v1/projects/clinic/assets"));
        await Json(await ci.GetAsync("/console/v1/projects/clinic/assets/releases/1"));
        Assert.Equal(0, (await Json(await ci.PostAsJsonAsync("/console/v1/projects/clinic/assets/contents/missing", new { hashes = new[] { Hash(en) } }))).GetProperty("missing").GetArrayLength());
        var second = await Json(await ci.PostAsJsonAsync("/console/v1/projects/clinic/assets/releases", new
        {
            note = "From CI", channel = "production",
            files = new Dictionary<string, object> { ["translations/en.json"] = new { hash = Hash(en), size = en.Length, mimeType = "" } },
        }));
        Assert.Equal(2, second.GetProperty("number").GetInt32());
        Assert.False((await ci.GetAsync("/console/v1/projects/clinic/assets/location")).IsSuccessStatusCode);
        Directory.Delete(files, recursive: true);
    }
}

[Collection("postgres")]
public class ServerEmailTests(PostgresFixture pg)
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Console_invites_never_go_through_a_projects_email_and_without_console_email_are_shared_as_links()
    {
        using var smtp = new FakeSmtp();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");

        // The project's Authentication sends email to its app users; the console's invites don't use it.
        var signin = await Json(await console.GetAsync("/console/v1/projects/clinic/auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Clinic", ["sendVerificationOnSignUp"] = true, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "smtp",
            ["smtp"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "127.0.0.1", ["port"] = smtp.Port, ["security"] = "none", ["username"] = "mailer", ["fromAddress"] = "no-reply@clinic.example" },
        };
        await Json(await console.PutAsJsonAsync("/console/v1/projects/clinic/auth/settings/signin", new { settings, smtpPassword = "pw", reason = "Email" }));

        var invite = await Json(await console.PostAsJsonAsync("/console/v1/invites", new { email = "colleague@example.com", role = "Admin", expiresInHours = 72 }));
        Assert.False(invite.GetProperty("emailed").GetBoolean());
        Assert.Contains("/invite?token=", invite.GetProperty("link").GetString());
        Assert.Empty(smtp.Received);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Console_emails_go_through_the_console_accounts_email_set_in_server_settings_authentication()
    {
        using var smtp = new FakeSmtp();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await TestProjects.CreateAsync(console, "clinic", "Clinic");

        // Server settings → Authentication → Settings → Emails to users: the console accounts' SMTP server, its password encrypted.
        var signin = await Json(await console.GetAsync("/console/v1/server/auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Winche", ["sendVerificationOnSignUp"] = false, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "smtp",
            ["smtp"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "127.0.0.1", ["port"] = smtp.Port, ["security"] = "none", ["username"] = "console-mailer", ["fromAddress"] = "console@winche.example", ["fromName"] = "Winche" },
        };
        await Json(await console.PutAsJsonAsync("/console/v1/server/auth/settings/signin", new { settings, smtpPassword = "pw", reason = "Console email" }));

        // Invites go through it.
        Assert.True((await Json(await console.PostAsJsonAsync("/console/v1/invites", new { email = "colleague@example.com", role = "Admin", expiresInHours = 72 }))).GetProperty("emailed").GetBoolean());
        var (user, password, data) = Assert.Single(smtp.Received);
        Assert.Equal(("console-mailer", "pw"), (user, password));
        Assert.Contains("To: colleague@example.com", data);
        Assert.Contains("From: Winche <console@winche.example>", data);
        Directory.Delete(files, recursive: true);
    }


    /// <summary>The server's own email: the AWS credentials and SES address of its environment, as a deployment sets them.</summary>
    private static IDisposable AwsEnvironment(FakeSes ses)
    {
        var vars = new Dictionary<string, string?> { ["AWS_ACCESS_KEY_ID"] = "AKIDTESTONLY", ["AWS_SECRET_ACCESS_KEY"] = "test-secret", ["AWS_ENDPOINT_URL_SESV2"] = ses.Url };
        var before = vars.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        return new Restore(() => { foreach (var (k, v) in before) Environment.SetEnvironmentVariable(k, v); });
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    [Fact]
    public async Task The_servers_own_email_sends_console_and_project_emails_with_its_environments_credentials()
    {
        using var ses = new FakeSes();
        using var env = AwsEnvironment(ses);
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"),
            ("Winche:Email:Provider", "ses"), ("Winche:Email:Region", "eu-west-1"), ("Winche:Email:FromAddress", "no-reply@winche.example"));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");

        // Console invites go out with nothing set up: no SMTP server, no keys saved.
        Assert.True((await Json(await console.PostAsJsonAsync("/console/v1/invites", new { email = "colleague@example.com", role = "Admin", expiresInHours = 72 }))).GetProperty("emailed").GetBoolean());
        var invite = Assert.Single(ses.Sent);
        Assert.Equal("colleague@example.com", invite["Destination"]!["ToAddresses"]![0]!.GetValue<string>());
        Assert.Equal("Winche console <no-reply@winche.example>", invite["FromEmailAddress"]!.GetValue<string>());
        Assert.Contains("/invite?token=", invite["Content"]!["Simple"]!["Body"]!["Text"]!["Data"]!.GetValue<string>());
        Assert.Contains("Credential=AKIDTESTONLY/", ses.Authorizations[0]);   // signed with the environment's credentials

        // A project's sign-in emails: the console offers the server's email, and sending through it needs no setup.
        var config = await Json(await console.GetAsync("/console/v1/projects/clinic/auth/config"));
        Assert.Equal("Amazon SES (eu-west-1) as no-reply@winche.example", config.GetProperty("serverEmail").GetString());
        var signin = await Json(await console.GetAsync("/console/v1/projects/clinic/auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Clinic", ["sendVerificationOnSignUp"] = false, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "app",
        };
        await Json(await console.PutAsJsonAsync("/console/v1/projects/clinic/auth/settings/signin", new { settings, reason = "Email through the server" }));
        var user = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/auth/users", new { email = "ann@example.com", password = "Ann-Passw0rd!" }));
        Assert.True((await console.PostAsync($"/console/v1/projects/clinic/auth/users/{user.GetProperty("uid").GetString()}/sendPasswordReset", null)).IsSuccessStatusCode);
        for (var i = 0; i < 100 && ses.Sent.Count < 2; i++) await Task.Delay(100);   // sent in the background
        var reset = ses.Sent[1];
        Assert.Equal("ann@example.com", reset["Destination"]!["ToAddresses"]![0]!.GetValue<string>());
        Assert.Equal("Clinic <no-reply@winche.example>", reset["FromEmailAddress"]!.GetValue<string>());
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Without_an_access_key_a_bucket_uses_the_servers_credentials_so_only_server_admins_may_choose_one()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards", products = new[] { "storage" } }));
        await Json(await admin.PostAsync("/console/v1/projects/wards/project/products/storage/start", null));
        for (var i = 0; i < 100 && !(await Json(await admin.GetAsync("/console/v1/projects/wards/project"))).GetProperty("products").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "storage" && p.GetProperty("ready").GetBoolean()); i++)
            await Task.Delay(100);

        // Bob is the project's admin, not the server's.
        var added = await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/members", new { email = "bob@example.com", role = "Admin" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(added.GetProperty("link").GetString()!).Query)["code"];
        await admin.PostAsJsonAsync("/console/v1/session/reset-password", new { code, newPassword = "Bob-Passw0rd!long" });
        var bob = server.CreateClient();
        await Json(await bob.PostAsJsonAsync("/console/v1/session/login", new { email = "bob@example.com", password = "Bob-Passw0rd!long" }));

        var keyless = new { kind = "s3", s3 = new { bucket = "server-bucket", region = "eu-west-1" } };
        var refused = await bob.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/test", new { location = keyless });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("only a server admin", await refused.Content.ReadAsStringAsync());
        refused = await bob.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/move", new { location = keyless, reason = "Mine now" });
        Assert.Contains("only a server admin", await refused.Content.ReadAsStringAsync());

        // With a key of its own, his test is run (and fails here: there's no such bucket), not refused.
        var keyed = await bob.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/test",
            new { location = new { kind = "s3", s3 = new { bucket = "wards-bucket", region = "eu-west-1", serviceUrl = "http://127.0.0.1:9", accessKey = "AKIDWARDS", forcePathStyle = true } }, secretKey = "s" });
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        // The server admin may use the server's own credentials.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/test",
            new { location = new { kind = "s3", s3 = new { bucket = "server-bucket", region = "eu-west-1", serviceUrl = "http://127.0.0.1:9", forcePathStyle = true } } })).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task An_app_user_disabled_or_deleted_loses_their_open_database_connection_at_once()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "database", "authentication");
        var app = server.CreateClient();

        async Task<(string Uid, System.Net.WebSockets.WebSocket Socket)> Connect(string email)
        {
            var session = await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email, password = "correct horse battery" }));
            var token = session.GetProperty("accessToken").GetString()!;
            var ws = server.Server.CreateWebSocketClient();
            var socket = await ws.ConnectAsync(new Uri(server.Server.BaseAddress, $"/projects/clinic/documents/v1/ws?access_token={token}"), CancellationToken.None);
            return (session.GetProperty("user").GetProperty("uid").GetString()!, socket);
        }

        async Task<System.Net.WebSockets.WebSocketCloseStatus?> ClosedWith(System.Net.WebSockets.WebSocket socket)
        {
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (true)
                {
                    var r = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return socket.CloseStatus;
                }
            }
            catch (OperationCanceledException) { return null; }
        }

        var (ann, annSocket) = await Connect("ann@example.com");
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, annSocket.State);
        await Json(await console.PostAsJsonAsync($"/console/v1/projects/clinic/auth/users/{ann}/disabled", new { disabled = true }));
        Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4401, await ClosedWith(annSocket));

        var (bob, bobSocket) = await Connect("bob@example.com");
        Assert.True((await console.DeleteAsync($"/console/v1/projects/clinic/auth/users/{bob}")).IsSuccessStatusCode);
        Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4401, await ClosedWith(bobSocket));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Http_functions_answer_on_the_projects_address_without_a_sign_in()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // functions need Deno
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"), ("Winche:ProjectsPath", projectsPath));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "clinic", "Clinic");
        await Json(await console.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards", products = new[] { "functions" } }));
        await Json(await console.PostAsync("/console/v1/projects/wards/project/products/functions/start", null));
        for (var i = 0; i < 100 && !(await Json(await console.GetAsync("/console/v1/projects/wards/project"))).GetProperty("products").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "functions" && p.GetProperty("ready").GetBoolean()); i++)
            await Task.Delay(100);
        await Json(await console.PostAsJsonAsync("/console/v1/projects/wards/functions/deploy", new { note = "Hook", files = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { http } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
                export const hook = http.onRequest(async (req) => ({ method: req.method, path: new URL(req.url).pathname, body: await req.text() }));
                """,
        } }));
        var anyone = server.CreateClient();
        var answer = await Json(await anyone.PostAsync("/projects/wards/functions/v1/http/hook/x", new StringContent("hello")));
        Assert.Equal(("POST", "/projects/wards/functions/v1/http/hook/x", "hello"),
            (answer.GetProperty("method").GetString(), answer.GetProperty("path").GetString(), answer.GetProperty("body").GetString()));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Functions_resources_are_sent_once_by_hash_and_read_by_the_deployment_naming_them()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // functions need Deno
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "functions");

        var font = new byte[2_000_000];
        new Random(7).NextBytes(font);
        var text = "Hallo"u8.ToArray();
        string Hash(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
        var missing = await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/resources/missing", new { hashes = new[] { Hash(font), Hash(text) } }));
        Assert.Equal(2, missing.GetProperty("missing").GetArrayLength());

        // A torn upload is refused; whole ones are kept, once.
        var torn = await admin.PutAsync($"/console/v1/projects/clinic/functions/resources/{Hash(font)}", new ByteArrayContent(font[..1000]));
        Assert.Equal(HttpStatusCode.BadRequest, torn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/console/v1/projects/clinic/functions/resources/{Hash(font)}", new ByteArrayContent(font))).StatusCode);
        missing = await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/resources/missing", new { hashes = new[] { Hash(font), Hash(text) } }));
        Assert.Equal([Hash(text)], missing.GetProperty("missing").EnumerateArray().Select(h => h.GetString()));

        var code = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, resources } from "@winche-technologies/winche-functions";
                const hello = resources.define("de/hello.txt"), font = resources.define("fonts/a.ttf");
                export const read = callable(async () => ({ text: await hello.text(), font: (await font.bytes())?.length }));
                """,
        };
        var resources = new Dictionary<string, object> { ["fonts/a.ttf"] = new { hash = Hash(font), size = font.Length }, ["de/hello.txt"] = new { hash = Hash(text), size = text.Length } };
        var refused = await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/deploy", new { note = "Too soon", files = code, resources });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("resources/de/hello.txt", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/console/v1/projects/clinic/functions/resources/{Hash(text)}", new ByteArrayContent(text))).StatusCode);
        await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/deploy", new { note = "Resources", files = code, resources }));
        var page = await Json(await admin.GetAsync("/console/v1/projects/clinic/functions"));
        Assert.Equal(["de/hello.txt", "fonts/a.ttf"], page.GetProperty("resources").EnumerateArray().Select(a => a.GetProperty("path").GetString()));
        var ran = await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/run", new { name = "read" }));
        Assert.Equal(("Hallo", 2_000_000), (ran.GetProperty("result").GetProperty("text").GetString(), ran.GetProperty("result").GetProperty("font").GetInt32()));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Secrets_may_be_references_to_aws_and_only_server_admins_have_them_read_with_the_servers_credentials()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // functions need Deno
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "wards", "Wards", "functions", "storage");
        var added = await Json(await admin.PostAsJsonAsync("/console/v1/projects/wards/members", new { email = "bob@example.com", role = "Admin" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(added.GetProperty("link").GetString()!).Query)["code"];
        await admin.PostAsJsonAsync("/console/v1/session/reset-password", new { code, newPassword = "Bob-Passw0rd!long" });
        var bob = server.CreateClient();   // the project's admin, not the server's
        await Json(await bob.PostAsJsonAsync("/console/v1/session/login", new { email = "bob@example.com", password = "Bob-Passw0rd!long" }));

        var sources = await Json(await bob.GetAsync("/console/v1/projects/wards/secrets/sources"));
        Assert.Equal(["aws-sm", "aws-ssm"], sources.GetProperty("sources").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.False(sources.GetProperty("appCredentials").GetBoolean());
        var key = Assert.Single(sources.GetProperty("keys").EnumerateArray());
        Assert.Equal(("secrets.source.aws", false), (key.GetProperty("name").GetString(), key.GetProperty("isSet").GetBoolean()));
        Assert.True((await Json(await admin.GetAsync("/console/v1/projects/wards/secrets/sources"))).GetProperty("appCredentials").GetBoolean());

        // Read with the server's credentials: only its admin may.
        var reference = new { reference = "aws-ssm:/wards/mail-key" };
        var refused = await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/MAIL_KEY", reference);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("server's own credentials", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/MAIL_KEY", reference)).StatusCode);
        var listed = (await Json(await bob.GetAsync("/console/v1/projects/wards/functions/secrets"))).GetProperty("secrets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "MAIL_KEY");
        Assert.Equal("aws-ssm:/wards/mail-key", listed.GetProperty("reference").GetString());

        // A bucket's secret key may be a reference too: the same rule.
        var bucket = new { kind = "s3", s3 = new { bucket = "wards-bucket", region = "eu-west-1", serviceUrl = "http://127.0.0.1:9", accessKey = "AKIDWARDS", forcePathStyle = true } };
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/test",
            new { location = bucket, secretKeyReference = "aws-ssm:/wards/bucket-key" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync("/console/v1/projects/wards/settings/location/files/test",
            new { location = bucket, secretKey = "s", secretKeyReference = "aws-ssm:/wards/bucket-key" })).StatusCode);

        // With the project's own AWS key, its admins may (and the key is never shown).
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync("/console/v1/projects/wards/secrets/keys/secrets.source.aws",
            new { values = new Dictionary<string, string> { ["accessKeyId"] = "AKIDWARDS" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync("/console/v1/projects/wards/secrets/keys/secrets.source.aws",
            new { values = new Dictionary<string, string> { ["accessKeyId"] = "AKIDWARDS", ["secretAccessKey"] = "wards-secret", ["region"] = "eu-west-2" } })).StatusCode);
        var withKey = await bob.GetStringAsync("/console/v1/projects/wards/secrets/sources");
        Assert.DoesNotContain("wards-secret", withKey);
        Assert.Contains("\"isSet\":true", withKey);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/SES_SECRET",
            new { reference = "aws-sm:arn:aws:secretsmanager:eu-west-2:123456789012:secret:wards/ses-AbCdEf#secretAccessKey" })).StatusCode);

        // What isn't a reference here says why; a value still works.
        var unknown = await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/OTHER", new { reference = "vault:/x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("use aws-sm or aws-ssm", await unknown.Content.ReadAsStringAsync());
        var tried = await Json(await bob.PostAsJsonAsync("/console/v1/projects/wards/secrets/test", new { reference = "aws-sm:arn:aws:s3:::bucket" }));
        Assert.False(tried.GetProperty("ok").GetBoolean());
        Assert.Contains("isn't a Secrets Manager ARN", tried.GetProperty("problem").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/OTHER", new { value = "plain" })).StatusCode);

        // Keeping secrets in AWS: only what the key may do is offered. Here AWS can't be asked (no AWS in tests): no rights,
        // and why; a request to keep one there anyway says so, and nothing changes.
        var access = (await Json(await bob.GetAsync("/console/v1/projects/wards/secrets/sources?fresh=true"))).GetProperty("sources")[0].GetProperty("access");
        Assert.False(access.GetProperty("write").GetBoolean());
        Assert.False(string.IsNullOrEmpty(access.GetProperty("problem").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/KEPT", new { reference = "keep:aws-sm" })).StatusCode);   // no value
        var notKept = await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/KEPT", new { value = "v", reference = "keep:aws-sm" });
        Assert.NotEqual(HttpStatusCode.NoContent, notKept.StatusCode);
        Assert.Contains("AWS Secrets Manager", await notKept.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync("/console/v1/projects/wards/functions/secrets/KEPT", new { value = "v", reference = "keep:vault" })).StatusCode);
        // The other secret fields take keep: beside their value too (refused here only because AWS can't be reached).
        await Json(await bob.PostAsJsonAsync("/console/v1/projects/wards/integrity/apps", new { name = "Web", id = "web-app", webSiteKey = "0x4AAAAAAAsitekey123" }));
        var turnstile = await bob.PutAsJsonAsync("/console/v1/projects/wards/integrity/apps/web-app/turnstile-secret", new { value = "0x4AAAAAAAsecretsecret", reference = "keep:aws-sm" });
        Assert.DoesNotContain("Give the widget's secret key", await turnstile.Content.ReadAsStringAsync());
        Assert.Contains("AWS Secrets Manager", await turnstile.Content.ReadAsStringAsync());
        var moved = await bob.PostAsJsonAsync("/console/v1/projects/wards/functions/secrets/OTHER/move", new { store = "aws-ssm" });
        Assert.NotEqual(HttpStatusCode.NoContent, moved.StatusCode);
        var still = (await Json(await bob.GetAsync("/console/v1/projects/wards/functions/secrets"))).GetProperty("secrets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "OTHER");
        Assert.Equal((JsonValueKind.Null, false), (still.GetProperty("reference").ValueKind, still.GetProperty("managed").GetBoolean()));   // still a value here
        // The Secrets page's list of what the products keep: functions' own and the source keys are listed elsewhere.
        var kept = await Json(await bob.GetAsync("/console/v1/projects/wards/secrets"));
        Assert.True(kept.GetProperty("available").GetBoolean());
        Assert.DoesNotContain(kept.GetProperty("secrets").EnumerateArray(), x => x.GetProperty("name").GetString()!.StartsWith("functions.secret.") || x.GetProperty("name").GetString()!.StartsWith("secrets.source."));
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync("/console/v1/projects/wards/secrets/keys/secrets.source.aws")).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task How_long_functions_run_is_the_servers_longest_then_the_projects_default_and_each_functions_own()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // functions need Deno
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "functions");
        await Json(await admin.PostAsJsonAsync("/console/v1/projects/clinic/functions/deploy", new { note = "Jobs", files = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, schedule } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
                export const quick = callable(async () => 1);
                export const backfill = schedule("0 3 * * *", async () => {}, { timeoutSeconds: 480 });
                """,
        } }));
        async Task<(int Max, int Quick, int Backfill)> Times()
        {
            var page = await Json(await admin.GetAsync("/console/v1/projects/clinic/functions"));
            int T(string n) => page.GetProperty("current").GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == n).GetProperty("timeoutSeconds").GetInt32();
            return (page.GetProperty("maxTimeoutSeconds").GetInt32(), T("quick"), T("backfill"));
        }
        Assert.Equal((540, 60, 480), await Times());

        // The project: a longer default, and the backfill's own time, up to the server's longest.
        var settings = System.Text.Json.Nodes.JsonNode.Parse("""{ "defaultTimeoutSeconds": 120, "timeouts": { "backfill": 1800 } }""")!;
        var refused = await admin.PutAsJsonAsync("/console/v1/projects/clinic/settings/functions", new { settings, reason = "Longer backfill" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("backfill: 1 second to 9 minutes", await refused.Content.ReadAsStringAsync());

        // The server allows longer runs: in force at once, with no restart.
        var page = await Json(await admin.GetAsync("/console/v1/server/settings"));
        var server1 = System.Text.Json.Nodes.JsonNode.Parse(page.GetProperty("settings").GetRawText())!.AsObject();
        server1["functionsMaxMinutes"] = 61;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = server1, reason = "x" })).StatusCode);
        server1["functionsMaxMinutes"] = 45;
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = server1, reason = "Long backfills" }));
        for (var i = 0; i < 100 && (await Times()).Max != 2700; i++) await Task.Delay(100);
        await Json(await admin.PutAsJsonAsync("/console/v1/projects/clinic/settings/functions", new { settings, reason = "Longer backfill" }));
        for (var i = 0; i < 100 && (await Times()).Quick != 120; i++) await Task.Delay(100);
        Assert.Equal((2700, 120, 1800), await Times());

        // Runs at once and memory: the server's limits reach the project at once; the functions memory is reported.
        Assert.True(page.GetProperty("functionsMemory").GetProperty("totalMb").GetInt32() > 0);
        server1["functionsMaxConcurrency"] = 0;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = server1, reason = "x" })).StatusCode);
        server1["functionsMaxConcurrency"] = 8;
        server1["functionsMaxMemoryMb"] = 512;
        await Json(await admin.PutAsJsonAsync("/console/v1/server/settings", new { settings = server1, reason = "Smaller tasks" }));
        for (var i = 0; i < 100 && (await Json(await admin.GetAsync("/console/v1/projects/clinic/functions"))).GetProperty("maxConcurrency").GetInt32() != 8; i++) await Task.Delay(100);
        var limits = await Json(await admin.GetAsync("/console/v1/projects/clinic/functions"));
        Assert.Equal((8, 512), (limits.GetProperty("maxConcurrency").GetInt32(), limits.GetProperty("maxMemoryMb").GetInt32()));

        // quick's instances on this server: a callable keeps one loaded.
        var instances = await Json(await admin.GetAsync("/console/v1/projects/clinic/functions/quick/instances"));
        Assert.Equal(1, instances.GetProperty("limits").GetProperty("minInstances").GetInt32());
        Assert.Equal(8, instances.GetProperty("limits").GetProperty("maxInstances").GetInt32());   // the default 10, held to the server's 8
        Assert.False(instances.GetProperty("process").GetProperty("own").GetBoolean());
        Assert.Equal(0, instances.GetProperty("otherServers").GetArrayLength());   // one server
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/console/v1/projects/clinic/functions/nothing/instances")).StatusCode);
        Directory.Delete(files, recursive: true);
    }
}
