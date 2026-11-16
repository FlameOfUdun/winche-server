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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (k, v) in extra) builder.UseSetting(k, v);
        builder.UseEnvironment("Production");
        builder.UseSetting("Winche:ConnectionString", connection);
        if (!extra.Any(e => e.Key == "Winche:MasterKey")) builder.UseSetting("Winche:MasterKey", "server-tests-master-key-0123456789abcdefghij");
        builder.UseSetting("Winche:PublicUrl", "http://localhost");
        if (!extra.Any(e => e.Key == "Winche:SetupCode")) builder.UseSetting("Winche:SetupCode", SetupCode);
        if (files is not null) builder.UseSetting("Winche:FilesPath", files);   // the first project kept in its own folder
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

        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/healthz")).StatusCode);
        Assert.Equal("/_console", (await app.GetAsync("/")).Headers.Location?.ToString());
        // Before setup, every product is off: nothing is published, and the APIs refuse.
        var setup = System.Text.Json.JsonDocument.Parse(await app.GetStringAsync("/.well-known/winche")).RootElement;
        Assert.Equal(0, setup.GetProperty("services").GetArrayLength());
        var off = await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("service-disabled", await off.Content.ReadAsStringAsync());

        // The first admin sets up the console and creates the project.
        var console = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" })).StatusCode);
        // The setup's second part: the project, with every product started (they're all off until then).
        Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/_console/api/project", new
        {
            name = "Clinic", id = "clinic", products = new[] { "database", "storage", "authentication", "functions", "hooks" },
        })).StatusCode);
        setup = System.Text.Json.JsonDocument.Parse(await app.GetStringAsync("/.well-known/winche")).RootElement;
        Assert.Equal("clinic", setup.GetProperty("projectId").GetString());
        Assert.Equal(["database", "storage", "auth", "functions"],
            setup.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray());

        // An app user signs up with Winche Auth (email and password are on to start with)…
        var session = await Json(await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        var uid = session.GetProperty("user").GetProperty("uid").GetString()!;
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var doc = new { fields = new { name = new { stringValue = "Ann" } } };
        // …but nothing is allowed until rules are published.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PutAsJsonAsync($"/documents/v1/{B64($"users/{uid}")}", doc)).StatusCode);

        // Every API refuses the same way: { error: { code, message, reason? } }, one list of codes, one HTTP table.
        async Task<(int, string, string?)> Refusal(HttpResponseMessage r)
        {
            var e = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
            Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString()));
            return ((int)r.StatusCode, e.GetProperty("code").GetString()!, e.TryGetProperty("reason", out var why) ? why.GetString() : null);
        }
        Assert.Equal((403, "permission-denied", null), await Refusal(await app.PutAsJsonAsync($"/documents/v1/{B64($"users/{uid}")}", doc)));
        Assert.Equal((403, "permission-denied", null), await Refusal(await app.PutAsJsonAsync($"/files/v1/{B64("a/b.txt")}", new { mimeType = "text/plain", sizeBytes = 1 })));
        Assert.Equal((401, "unauthenticated", "INVALID_CREDENTIALS"), await Refusal(await app.PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@example.com", password = "wrong one, long enough" })));
        Assert.Equal((404, "not-found", null), await Refusal(await app.PostAsJsonAsync("/functions/v1/call/nothing", new { data = (object?)null })));

        // The admin publishes rules in the console.
        var parsed = await Json(await console.PostAsJsonAsync("/_console/api/rules/text/parse",
            new { text = "match users/{uid} { allow read, write: if request.auth.uid == uid; }" }));
        await Json(await console.PostAsJsonAsync("/_console/api/rules/database", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Users own their document" }));

        Assert.Equal(HttpStatusCode.OK, (await app.PutAsJsonAsync($"/documents/v1/{B64($"users/{uid}")}", doc)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PutAsJsonAsync($"/documents/v1/{B64("users/someone-else")}", doc)).StatusCode);

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
        await Json(await http.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await http.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await http.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "database" } }));

        async Task<string?> Encoding(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.ParseAdd("br, gzip");
            using var response = await http.SendAsync(request);
            return response.Content.Headers.ContentEncoding.FirstOrDefault();
        }
        // The apps' APIs (a bearer token, never a cookie) and the console's page and files: compressed.
        Assert.Equal("br", await Encoding("/documents/v1/" + Convert.ToBase64String("notes/n1"u8.ToArray())));
        Assert.Equal("br", await Encoding("/_console/"));
        using (var query = new HttpRequestMessage(HttpMethod.Post, "/documents/v1:runQuery") { Content = JsonContent.Create(new { query = new { collection = "notes" } }) })
        {
            query.Headers.AcceptEncoding.ParseAdd("br");
            using var answer = await http.SendAsync(query);
            Assert.Equal("br", answer.Content.Headers.ContentEncoding.FirstOrDefault());   // the colon verbs too
        }
        // The console's API and sign-in (cookies): never, so an answer's size can't give away what it holds.
        Assert.Null(await Encoding("/_console/api/auth/state"));
        Assert.Null(await Encoding("/_console/api/project"));
        Assert.Null(await Encoding("/auth/v1/config"));
    }

    [Fact]
    public async Task Each_project_has_its_own_browser_origins_besides_its_registered_web_apps()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));

        async Task<string?> Allowed(string origin, string prefix = "")
        {
            var preflight = new HttpRequestMessage(HttpMethod.Options, $"{prefix}/auth/v1/signIn");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            var response = await server.CreateClient().SendAsync(preflight);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
        }

        // None set: any origin. Then the project's own, saved with a reason.
        Assert.Equal("https://anything.example.com", await Allowed("https://anything.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PutAsJsonAsync("/_console/api/apps/browser-origins", new { origins = new[] { "not an origin" }, reason = "x" })).StatusCode);
        await Json(await console.PutAsJsonAsync("/_console/api/apps/browser-origins", new { origins = new[] { "https://configured.example.com/" }, reason = "Our admin page" }));
        Assert.Equal("https://configured.example.com", (await Json(await console.GetAsync("/_console/api/apps/registered"))).GetProperty("browserOrigins")[0].GetString());
        Assert.Equal("https://configured.example.com", await Allowed("https://configured.example.com"));
        Assert.Null(await Allowed("https://app.example.com"));
        // Another project keeps its own (none: any).
        await Json(await console.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));
        Assert.Equal("https://app.example.com", await Allowed("https://app.example.com", "/p/wards"));
        await Json(await console.PostAsJsonAsync("/_console/api/apps/registered", new { name = "Clinic web", webOrigins = new[] { "https://app.example.com" } }));
        Assert.Equal("https://app.example.com", await Allowed("https://app.example.com"));
        Assert.Null(await Allowed("https://elsewhere.example.com"));
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
            await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
            await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
            await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));

            // A second project, made from the console's switcher: its own database, made by the server.
            Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/_console/api/projects", new { name = "Again", id = "clinic" })).StatusCode);
            var created = await Json(await console.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));
            Assert.Equal("/p/wards/_console", created.GetProperty("consoleUrl").GetString());
            // Its console's page loads its own code and API: the base address carries the project's path.
            Assert.Contains("<base href=\"/p/wards/_console/\">", await console.GetStringAsync("/p/wards/_console/overview"));

            // Every project's console lists them all, with their consoles' addresses and which one it is.
            var listed = await Json(await console.GetAsync("/p/wards/_console/api/projects"));
            Assert.Equal("wards", listed.GetProperty("current").GetString());
            Assert.True(listed.GetProperty("canCreate").GetBoolean());
            Assert.Equal(["clinic:/_console", "wards:/p/wards/_console"], listed.GetProperty("projects").EnumerateArray()
                .Select(p => $"{p.GetProperty("id").GetString()}:{p.GetProperty("consoleUrl").GetString()}").ToArray());
            // With the products each has started (the Projects home shows them).
            Assert.Equal(["clinic:authentication", "wards:"], listed.GetProperty("projects").EnumerateArray()
                .Select(p => $"{p.GetProperty("id").GetString()}:{string.Join(",", p.GetProperty("products").EnumerateArray().Select(x => x.GetString()))}").ToArray());
            // The default project seen through its own path still points at the root.
            Assert.Equal("/_console", (await Json(await console.GetAsync("/p/clinic/_console/api/projects"))).GetProperty("projects")[0].GetProperty("consoleUrl").GetString());

            // The same console sign-in works there; the project is made, named, with nothing started.
            var wards = await Json(await console.GetAsync("/p/wards/_console/api/project"));
            Assert.True(wards.GetProperty("created").GetBoolean());
            Assert.Equal("Wards", wards.GetProperty("name").GetString());
            Assert.All(wards.GetProperty("products").EnumerateArray(), p => Assert.False(p.GetProperty("started").GetBoolean()));
            await StartProductAsync(console, "/p/wards", "authentication");

            // The default project answers at the root (as before) and at its own path.
            Assert.Equal("clinic", (await Json(await console.GetAsync("/p/clinic/_console/api/project"))).GetProperty("id").GetString());
            var wellKnown = await Json(await console.GetAsync("/p/wards/.well-known/winche"));
            Assert.Equal("wards", wellKnown.GetProperty("projectId").GetString());
            Assert.Equal("http://localhost/p/wards", wellKnown.GetProperty("baseUrl").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await console.GetAsync("/p/nope/.well-known/winche")).StatusCode);

            // Users are each project's own: one signed up in Clinic doesn't exist in Wards, and the same email signs
            // up again there as somebody else.
            var app = server.CreateClient();
            var ann = await Json(await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
            Assert.NotEqual(HttpStatusCode.OK, (await app.PostAsJsonAsync("/p/wards/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" })).StatusCode);
            var annInWards = await Json(await app.PostAsJsonAsync("/p/wards/auth/v1/signUp", new { email = "ann@example.com", password = "another horse battery" }));
            Assert.NotEqual(ann.GetProperty("user").GetProperty("uid").GetString(), annInWards.GetProperty("user").GetProperty("uid").GetString());

            // Settings are each project's own too.
            await Json(await console.PutAsJsonAsync("/p/wards/_console/api/project", new { name = "Ward boards", id = "wards" }));
            Assert.Equal("Clinic", (await Json(await console.GetAsync("/_console/api/project"))).GetProperty("name").GetString());
        }

        // After a restart, the server starts every project again.
        await using (var again = new ServerFactory(db, files, projectsSetting))
        {
            var console = again.CreateClient();
            await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
            Assert.Equal("Ward boards", (await Json(await console.GetAsync("/p/wards/_console/api/project"))).GetProperty("name").GetString());
            Assert.Equal(HttpStatusCode.OK, (await console.PostAsJsonAsync("/p/wards/auth/v1/signIn", new { email = "ann@example.com", password = "another horse battery" })).StatusCode);
        }
    }

    [Fact]
    public async Task A_created_project_has_its_own_members_and_others_see_only_their_projects()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:ProjectsPath", projectsPath));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));

        // The server admin is an admin of the new project without being listed; it has no members yet.
        var members = await Json(await admin.GetAsync("/p/wards/_console/api/members"));
        Assert.Equal(0, members.GetProperty("members").GetArrayLength());
        Assert.Equal("admin@example.com", members.GetProperty("serverAdmins")[0].GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/_console/api/members")).StatusCode);   // the first project goes by roles

        // Bob is new: added as a Member, he gets an account and a link to set his password (no email set up here).
        var added = await Json(await admin.PostAsJsonAsync("/p/wards/_console/api/members", new { email = "bob@example.com", role = "Member" }));
        Assert.True(added.GetProperty("newAccount").GetBoolean());
        var link = new Uri(added.GetProperty("link").GetString()!);
        // Console accounts are the server's: their links open the server's console (…/_console/reset-password?mode=resetPassword&code=…).
        Assert.Equal("/_console/reset-password", link.AbsolutePath);
        var code = System.Web.HttpUtility.ParseQueryString(link.Query)["code"];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/p/wards/_console/api/auth/reset-password",
            new { code, newPassword = "Bob-Passw0rd!long" })).StatusCode);

        var bob = server.CreateClient();
        await Json(await bob.PostAsJsonAsync("/p/wards/_console/api/auth/login", new { email = "bob@example.com", password = "Bob-Passw0rd!long" }));
        var here = await Json(await bob.GetAsync("/p/wards/_console/api/auth/state"));
        Assert.Equal("Member", here.GetProperty("user").GetProperty("role").GetString());
        Assert.True(here.GetProperty("user").GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/p/wards/_console/api/project")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/p/wards/_console/api/members")).StatusCode);   // members aren't admins

        // In the first project he's nobody: signed in, no role, refused.
        var there = await Json(await bob.GetAsync("/_console/api/auth/state"));
        Assert.False(there.GetProperty("user").GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/_console/api/project")).StatusCode);

        // His switcher lists only Wards, and he can't create projects.
        var mine = await Json(await bob.GetAsync("/p/wards/_console/api/projects"));
        Assert.Equal(["wards"], mine.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.False(mine.GetProperty("canCreate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/p/wards/_console/api/projects", new { name = "Mine", id = "mine" })).StatusCode);

        // winche login signs in once, at the server's console: he approves there though he has no role in the first
        // project, and the CLI's session works in Wards with his role there (and lists his projects at the root).
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var approved = await Json(await bob.PostAsJsonAsync("/_console/api/cli-login/approve", new { challenge, port = 9005, state = "s", name = "winche CLI on laptop" }));
        var cliCode = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var session = await Json(await server.CreateClient().PostAsJsonAsync("/_console/api/cli-login/token", new { code = cliCode, verifier }));
        var cli = server.CreateClient();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/p/wards/_console/api/project")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/p/wards/_console/api/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/_console/api/project")).StatusCode);
        Assert.Equal(["wards"], (await Json(await cli.GetAsync("/_console/api/projects"))).GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());

        // The server admin's CLI makes projects (winche projects:create).
        var adminApproved = await Json(await admin.PostAsJsonAsync("/_console/api/cli-login/approve", new { challenge, port = 9005, state = "a", name = "winche CLI on desk" }));
        var adminCode = System.Web.HttpUtility.ParseQueryString(new Uri(adminApproved.GetProperty("redirect").GetString()!).Query)["code"];
        var adminSession = await Json(await server.CreateClient().PostAsJsonAsync("/_console/api/cli-login/token", new { code = adminCode, verifier }));
        var adminCli = server.CreateClient();
        adminCli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSession.GetProperty("accessToken").GetString());
        Assert.Equal("beds", (await Json(await adminCli.PostAsJsonAsync("/_console/api/projects", new { name = "Beds", id = "beds" }))).GetProperty("id").GetString());
        // Its settings and CI tokens need two-step sign-in like the browser's (winche 2fa enable).
        var refused = await adminCli.PostAsJsonAsync("/p/beds/_console/api/tokens", new { name = "gitlab", scopes = new[] { "rules" }, days = 7 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("TWO_FACTOR_REQUIRED", await refused.Content.ReadAsStringAsync());

        // Made an Admin of Wards, he manages its members; removed, he's out.
        var bobId = (await Json(await admin.GetAsync("/p/wards/_console/api/members"))).GetProperty("members")[0].GetProperty("userId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/p/wards/_console/api/members/{bobId}", new { role = "Admin" })).StatusCode);
        await Task.Delay(100);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/p/wards/_console/api/members")).StatusCode);
        // His CLI too: an admin of Wards, but not of the server (he can't make projects, or see the first one).
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/p/wards/_console/api/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.PostAsJsonAsync("/p/wards/_console/api/projects", new { name = "Mine", id = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/_console/api/project")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/p/wards/_console/api/members/{bobId}")).StatusCode);
        Assert.False((await Json(await bob.GetAsync("/p/wards/_console/api/auth/state"))).GetProperty("user").GetProperty("member").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.GetAsync("/p/wards/_console/api/project")).StatusCode);   // nor can his CLI
    }

    [Fact]
    public async Task A_project_is_archived_then_restored_with_its_data_or_deleted_for_good()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var db = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(db, files, ("Winche:ProjectsPath", projectsPath));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));
        await Json(await admin.PostAsJsonAsync("/p/wards/_console/api/members", new { email = "bob@example.com", role = "Viewer" }));

        // The first project can't be archived.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/_console/api/projects/clinic/archive", null)).StatusCode);

        // Archived from its own console: its address is refused, it's listed as archived, and its id is taken.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/p/wards/_console/api/projects/wards/archive", null)).StatusCode);
        var gone = await admin.GetAsync("/p/wards/_console/api/project");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains("project-archived", await gone.Content.ReadAsStringAsync());
        var listed = await Json(await admin.GetAsync("/_console/api/projects"));
        Assert.Equal(["clinic"], listed.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal("wards", listed.GetProperty("archived")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Again", id = "wards" })).StatusCode);
        // Only archived projects are deleted.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/_console/api/projects/clinic")).StatusCode);

        // Restored: back at its address, with its members.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/_console/api/projects/wards/restore", null)).StatusCode);
        Assert.Equal("bob@example.com", (await Json(await admin.GetAsync("/p/wards/_console/api/members"))).GetProperty("members")[0].GetProperty("email").GetString());

        // Archived again and deleted: its database is dropped, its address unknown, its id free again.
        await admin.PostAsync("/_console/api/projects/wards/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/_console/api/projects/wards")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/p/wards/_console/api/project")).StatusCode);
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", conn);
            cmd.Parameters.AddWithValue(server.Services.GetRequiredService<ProjectHosts>().DatabaseName("wards"));
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.False(Directory.Exists(Path.Combine(projectsPath, "wards")));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards anew", id = "wards" }));
        Assert.Equal(0, (await Json(await admin.GetAsync("/p/wards/_console/api/members"))).GetProperty("members").GetArrayLength());
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
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));
        await StartProductAsync(admin, "/p/wards", "authentication");
        await Json(await admin.PostAsJsonAsync("/p/wards/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        await Json(await admin.PostAsJsonAsync("/p/wards/_console/api/apps/registered", new { name = "Ward app", id = "ward-app" }));

        // The control database: accounts, keys and the project list in public; each project's configuration in its schema.
        var publicTables = await TablesAsync(control, "public");
        Assert.Contains("winche_console_invites", publicTables);
        Assert.DoesNotContain("AspNetUsers", publicTables);   // console accounts are a Winche Auth (console_accounts)
        Assert.Contains("winche_server_projects", publicTables);
        Assert.DoesNotContain("winche_settings", publicTables);
        Assert.Contains("winche_settings", await TablesAsync(control, "project_default"));
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
        await admin.PostAsync("/_console/api/projects/wards/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/_console/api/projects/wards")).StatusCode);
        Assert.Empty(await TablesAsync(control, "project_wards"));
    }

    [Fact]
    public async Task Server_settings_start_from_the_deployment_and_changes_in_the_console_win()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));

        // What the deployment set is where they start.
        var page = await Json(await admin.GetAsync("/_console/api/server/settings"));
        Assert.Equal("deployment", page.GetProperty("source").GetString());
        Assert.Equal("http://localhost", page.GetProperty("deployment").GetProperty("publicUrl").GetString());
        Assert.False(page.GetProperty("settings").GetProperty("requireTwoFactorForSettings").GetBoolean());

        // The public address is the deployment's: the console can't change it (the issuer of every token).
        var settings = System.Text.Json.Nodes.JsonNode.Parse(page.GetProperty("settings").GetRawText())!.AsObject();
        settings["publicUrl"] = "https://api.example.com";
        var refused = await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings, reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Winche__PublicUrl", await refused.Content.ReadAsStringAsync());

        // Other settings are saved with a reason, and the console's value wins.
        settings["publicUrl"] = "http://localhost";
        settings["consolePasswordsOnlyForAdmins"] = true;
        settings["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "staff", ["enabled"] = true, ["issuer"] = "https://id.example.com/realms/staff", ["clientId"] = "winche-console",
        });
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings, reason = "Our web app" }));
        page = await Json(await admin.GetAsync("/_console/api/server/settings"));
        Assert.True(page.GetProperty("settings").GetProperty("consolePasswordsOnlyForAdmins").GetBoolean());
        Assert.Equal("console", page.GetProperty("source").GetString());
        Assert.Equal("Our web app", page.GetProperty("version").GetProperty("reason").GetString());
        Assert.Equal("http://localhost", page.GetProperty("settings").GetProperty("publicUrl").GetString());

        // Passwords are write-only.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/_console/api/server/secrets/server.consoleSignIn.staff.clientSecret", new { value = "s3cret" })).StatusCode);
        var secret = (await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("secrets")[0];
        Assert.NotEqual(JsonValueKind.Null, secret.GetProperty("setAt").ValueKind);
        Assert.DoesNotContain("s3cret", await (await admin.GetAsync("/_console/api/server/settings")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/_console/api/server/secrets/anything", new { value = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_new_project_has_its_data_on_the_database_server_chosen_when_its_made()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var projectsPath = Directory.CreateTempSubdirectory("winche-server-projects-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:ProjectsPath", projectsPath), ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));

        // "Another" server: the test's PostgreSQL by its address, user and password, as a separate database server would be.
        var target = new NpgsqlConnectionStringBuilder(control);
        var database = new { host = target.Host, port = target.Port, username = target.Username, password = target.Password, sslMode = "Disable", database = "wards_data" };

        // Checked first: a wrong password says why; the right one is ready, the database to be made.
        var wrong = await Json(await admin.PostAsJsonAsync("/_console/api/projects/check-database", new { id = "wards", database = database with { password = "nope" } }));
        Assert.False(wrong.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Couldn't connect", wrong.GetProperty("problem").GetString());
        var ready = await Json(await admin.PostAsJsonAsync("/_console/api/projects/check-database", new { id = "wards", database }));
        Assert.True(ready.GetProperty("ok").GetBoolean());
        Assert.False(ready.GetProperty("exists").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards", database = database with { password = "nope" } })).StatusCode);

        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards", database }));
        Assert.Equal("Wards", (await Json(await admin.GetAsync("/p/wards/_console/api/project"))).GetProperty("name").GetString());
        // The project list says where its database is; its password is a secret of its own.
        await using var conn = new NpgsqlConnection(control);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT db_host, db_port, db_username, database FROM winche_server_projects WHERE id = 'wards'", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal((target.Host, target.Port, target.Username, "wards_data"), (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
        await r.DisposeAsync();

        // Without a database server: one of its own on the server's PostgreSQL, as before.
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Labs", id = "labs" }));
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
            await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
            var login = await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" });
            cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
            admin.DefaultRequestHeaders.Add("Cookie", cookie);
            await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));
            await AddProviderAsync(admin, new FakeOidcProvider());   // a server secret: its client secret
            await Json(await before.CreateClient().PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        }

        // The new key with the old one as previous: the console sign-in, the app's users and the secrets all carry on.
        await using (var rotating = new ServerFactory(db, files, Key(newKey), ("Winche:PreviousMasterKeys:0", oldKey), TwoStepOff))
        {
            var admin = rotating.CreateClient(new() { HandleCookies = false });
            admin.DefaultRequestHeaders.Add("Cookie", cookie);
            // Started with the old key as previous: every secret moved to the new one (nothing for the console to do).
            await Json(await admin.GetAsync("/_console/api/server/settings"));
            await Json(await rotating.CreateClient().PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        }

        // Then the new key alone: nothing needed the old one any more.
        await using var after = new ServerFactory(db, files, Key(newKey), TwoStepOff);
        var again = after.CreateClient(new() { HandleCookies = false });
        again.DefaultRequestHeaders.Add("Cookie", cookie);
        var final = await Json(await again.GetAsync("/_console/api/server/settings"));
        Assert.NotEqual(JsonValueKind.Null, final.GetProperty("secrets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "server.consoleSignIn.staff.clientSecret").GetProperty("setAt").ValueKind);
        await Json(await after.CreateClient().PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task A_request_body_that_cant_be_read_is_refused_with_the_reason()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication", "database" } }));
        var app = server.CreateClient();
        foreach (var (path, body) in new[]
        {
            ("/auth/v1/signUp", "{oops"),
            ("/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), """{"fields":{"x":{"bogusValue":1}}}"""),
            ("/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), "[1,2]"),
            ("/documents/v1/" + Convert.ToBase64String("a/b"u8.ToArray()).TrimEnd('='), "{}"),
        })
        {
            var resp = await app.SendAsync(new HttpRequestMessage(path.StartsWith("/doc") ? HttpMethod.Put : HttpMethod.Post, path)
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
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));

        var signIn = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["scopes"] = "openid profile email", ["buttonLabel"] = "Sign in with Staff ID",
            ["roleClaim"] = "groups", ["adminValues"] = new System.Text.Json.Nodes.JsonArray("winche-admins"), ["memberValues"] = new System.Text.Json.Nodes.JsonArray("staff"),
            ["viewerValues"] = new System.Text.Json.Nodes.JsonArray(), ["createAccounts"] = true, ["trustProviderTwoStep"] = true, ["id"] = "staff",
        };
        idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true, ["given_name"] = "Bob", ["groups"] = new[] { "staff" } };

        // A test sign-in with the unsaved settings: what came back and the role it maps to; nobody signed in.
        var test = await Json(await admin.PostAsJsonAsync("/_console/api/server/console-sign-in/test", new { settings = signIn, clientSecret = idp.ClientSecret }));
        Assert.Equal("http://localhost/_console/api/auth/oidc/callback", test.GetProperty("callbackUrl").GetString());
        var tester = server.CreateClient(new() { AllowAutoRedirect = false });
        var report = await FollowAsync(tester, idp, "/_console/" + test.GetProperty("url").GetString());
        var html = await report.Content.ReadAsStringAsync();
        Assert.Contains("winche-oidc-test", html);
        Assert.Contains("\"role\":\"Member\"", html);
        Assert.Contains("bob@example.com", html);

        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(signIn);
        page["consolePasswordsOnlyForAdmins"] = true;
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = page, reason = "Staff ID" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/_console/api/server/secrets/server.consoleSignIn.staff.clientSecret", new { value = idp.ClientSecret })).StatusCode);

        // The sign-in page offers it.
        var anonymous = await Json(await server.CreateClient().GetAsync("/_console/api/auth/state"));
        var offered = anonymous.GetProperty("oidc").GetProperty("providers").EnumerateArray().Single();
        Assert.Equal(("staff", "Sign in with Staff ID"), (offered.GetProperty("id").GetString(), offered.GetProperty("label").GetString()));

        // Bob signs in: a console account is made for him, a Member from his group, and two-step is the provider's.
        var bob = server.CreateClient(new() { AllowAutoRedirect = false });
        var landed = await FollowAsync(bob, idp, "/_console/api/auth/oidc/start?provider=staff&returnUrl=/p/clinic/_console/");
        Assert.Equal("/p/clinic/_console/", landed.Headers.Location!.OriginalString);
        var state = (await Json(await bob.GetAsync("/_console/api/auth/state"))).GetProperty("user");
        Assert.Equal(("bob@example.com", "Member", true), (state.GetProperty("email").GetString(), state.GetProperty("role").GetString(), state.GetProperty("signedInWithProvider").GetBoolean()));
        // At once, from his first sign-in (the one that made his account): his role works for what it allows.
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/_console/api/project")).StatusCode);

        // His groups change at the provider: the next sign-in brings it (here only in the access token, as Keycloak does).
        idp.Next = new() { ["sub"] = "u-1", ["email"] = "bob@example.com", ["email_verified"] = true };
        idp.NextAccess = new() { ["sub"] = "u-1", ["groups"] = new[] { "staff", "winche-admins" } };
        var bob2 = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(bob2, idp, "/_console/api/auth/oidc/start");
        Assert.True((await Json(await bob2.GetAsync("/_console/api/auth/state"))).GetProperty("user").GetProperty("serverAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bob2.GetAsync("/_console/api/server/settings")).StatusCode);   // admin now, at once

        // Passwords are for server admins only now; the built-in admin still gets in (the way in when the provider is down).
        await Json(await server.CreateClient().PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));

        // An address already taken that the provider doesn't vouch for isn't linked.
        idp.NextAccess = null;
        idp.Next = new() { ["sub"] = "u-2", ["email"] = "admin@example.com", ["email_verified"] = false };
        var mallory = server.CreateClient(new() { AllowAutoRedirect = false });
        var refused = await FollowAsync(mallory, idp, "/_console/api/auth/oidc/start");
        Assert.Contains("signInError=", refused.Headers.Location!.OriginalString);
        Assert.Equal(JsonValueKind.Null, (await Json(await mallory.GetAsync("/_console/api/auth/state"))).GetProperty("user").ValueKind);
        Directory.Delete(files, recursive: true);
    }

    /// <summary>Starts a product in a project's console and waits for its setup to finish.</summary>
    internal static async Task StartProductAsync(HttpClient console, string prefix, string product)
    {
        await Json(await console.PostAsync($"{prefix}/_console/api/project/products/{product}/start", null));
        for (var i = 0; i < 100; i++)
        {
            var run = await Json(await console.GetAsync($"{prefix}/_console/api/project/products/{product}/setup"));
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
        var state = await Json(await console.GetAsync("/_console/api/auth/state"));
        Assert.True(state.GetProperty("setup").GetProperty("code").GetBoolean());

        var account = new { email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" };
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/_console/api/auth/setup", account)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/_console/api/auth/setup/code", new { setupCode = "AAAA-BBBB-CCCC" })).StatusCode);

        // The code the server made (and logged): accepted however it's typed.
        var code = await server.Services.GetRequiredService<ServerSettingsProvider>().SetupCodeAsync(CancellationToken.None);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal(HttpStatusCode.NoContent, (await console.PostAsJsonAsync("/_console/api/auth/setup/code", new { setupCode = code.ToLowerInvariant().Replace("-", " ") })).StatusCode);
        await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = code, account.email, account.firstName, account.lastName, account.password }));

        // Set up: no more setup, and the code isn't asked for again.
        state = await Json(await console.GetAsync("/_console/api/auth/state"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("setup").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await console.PostAsJsonAsync("/_console/api/auth/setup/code", new { setupCode = code })).StatusCode);
    }

    /// <summary>A provider in Server settings, its secret saved: "staff", at <paramref name="idp"/>.</summary>
    private static async Task AddProviderAsync(HttpClient admin, FakeOidcProvider idp, string id = "staff", string label = "Sign in with Staff ID")
    {
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        var list = page["consoleProviders"]?.AsArray() ?? [];
        list.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id, ["enabled"] = true, ["issuer"] = idp.Issuer, ["clientId"] = idp.ClientId, ["buttonLabel"] = label, ["createAccounts"] = true,
        });
        page["consoleProviders"] = list.DeepClone();
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = page, reason = "Add " + id }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/_console/api/server/secrets/server.consoleSignIn.{id}.clientSecret", new { value = idp.ClientSecret })).StatusCode);
    }

    private static async Task<HttpClient> AdminAsync(ServerFactory server)
    {
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        return admin;
    }

    private static async Task<string?> SignedInAsync(HttpClient client) =>
        (await Json(await client.GetAsync("/_console/api/auth/state"))).GetProperty("user") is { ValueKind: JsonValueKind.Object } u ? u.GetProperty("email").GetString() : null;

    [Fact]
    public async Task The_console_offers_several_identity_providers_and_one_person_is_the_same_account_through_each()
    {
        await using var staff = new FakeOidcProvider();
        await using var partner = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await AddProviderAsync(admin, staff);
        await AddProviderAsync(admin, partner, "partner", "Sign in with Partner ID");

        var offered = (await Json(await server.CreateClient().GetAsync("/_console/api/auth/state"))).GetProperty("oidc").GetProperty("providers")
            .EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(["staff", "partner"], offered);
        Assert.Equal(HttpStatusCode.NotFound, (await server.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/_console/api/auth/oidc/start")).StatusCode);   // which one?

        staff.Next = new() { ["sub"] = "s-1", ["email"] = "bob@example.com", ["email_verified"] = true };
        partner.Next = new() { ["sub"] = "p-9", ["email"] = "bob@example.com", ["email_verified"] = true };
        var viaStaff = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(viaStaff, staff, "/_console/api/auth/oidc/start?provider=staff");
        var viaPartner = server.CreateClient(new() { AllowAutoRedirect = false });
        await FollowAsync(viaPartner, partner, "/_console/api/auth/oidc/start?provider=partner");
        var a = (await Json(await viaStaff.GetAsync("/_console/api/auth/state"))).GetProperty("user").GetProperty("id").GetString();
        var b = (await Json(await viaPartner.GetAsync("/_console/api/auth/state"))).GetProperty("user").GetProperty("id").GetString();
        Assert.Equal(a, b);

        // A provider taken off the list: its sessions end, and its secret goes with it.
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(page["consoleProviders"]!.AsArray().First(p => (string?)p!["id"] == "staff")!.DeepClone());
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = page, reason = "No more partners" }));
        Assert.Null(await SignedInAsync(viaPartner));
        Assert.Equal("bob@example.com", await SignedInAsync(viaStaff));
        var secrets = (await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("secrets").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
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
            await FollowAsync(bob, idp, "/_console/api/auth/oidc/start");
            Assert.Equal("bob@example.com", await SignedInAsync(bob));
            Assert.Equal("bob@example.com", await SignedInAsync(bob));
            Assert.True(idp.Refreshes >= 2);

            // Signed out at the provider: the provider won't renew it, and the console's session ends.
            idp.SignOut("u-1");
            Assert.Null(await SignedInAsync(bob));

            // Back-channel logout of his session: it ends at once; a session from another sign-in stays.
            var again = server.CreateClient(new() { AllowAutoRedirect = false });
            await FollowAsync(again, idp, "/_console/api/auth/oidc/start");
            var other = server.CreateClient(new() { AllowAutoRedirect = false });
            await FollowAsync(other, idp, "/_console/api/auth/oidc/start");
            Assert.Equal(HttpStatusCode.BadRequest, (await server.CreateClient().PostAsync("/_console/api/auth/oidc/backchannel-logout",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = "not-a-token" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().PostAsync("/_console/api/auth/oidc/backchannel-logout",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = idp.LogoutToken(idp.LastSid, null) }))).StatusCode);
            Assert.Null(await SignedInAsync(other));
            Assert.Equal("bob@example.com", await SignedInAsync(again));
            // All of his sessions (by subject).
            Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().PostAsync("/_console/api/auth/oidc/backchannel-logout",
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
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));
        await Json(await server.CreateClient().PostAsJsonAsync("/auth/v1/signUp", new { email = "patient@example.com", password = "correct horse battery" }));

        // The console's accounts, not the app's users.
        var users = await Json(await admin.GetAsync("/_console/api/server/auth/users"));
        Assert.Equal(["admin@example.com"], users.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!));
        var appUsers = await Json(await admin.GetAsync("/_console/api/app-auth/users"));
        Assert.Equal(["patient@example.com"], appUsers.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!));

        // Its configuration, templates and sign-in settings are the accounts' own.
        Assert.True((await Json(await admin.GetAsync("/_console/api/server/auth/config"))).GetProperty("wincheAuth").GetBoolean());
        Assert.True((await Json(await admin.GetAsync("/_console/api/server/auth/email-templates"))).GetProperty("templates").GetArrayLength() > 0);
        var signin = await Json(await admin.GetAsync("/_console/api/server/auth/settings/signin"));
        Assert.Equal(8, signin.GetProperty("settings").GetProperty("emailPassword").GetProperty("minLength").GetInt32());   // the console's minimum

        // Only server admins.
        await Json(await admin.PostAsJsonAsync("/_console/api/users", new { email = "member@example.com", role = "Member", password = "Passw0rd!long" }));
        var member = server.CreateClient();
        await Json(await member.PostAsJsonAsync("/_console/api/auth/login", new { email = "member@example.com", password = "Passw0rd!long" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/_console/api/server/auth/users")).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task The_consoles_identity_providers_are_the_console_accounts_sign_in_providers_with_their_keycloak_sync()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        var page = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/_console/api/server/settings"))).GetProperty("settings").GetRawText())!.AsObject();
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "staff", ["enabled"] = true, ["issuer"] = "https://id.example.com/realms/staff", ["clientId"] = "winche-console",
            ["buttonLabel"] = "Sign in with Staff ID", ["trustProviderTwoStep"] = true,
            ["keycloakSync"] = new System.Text.Json.Nodes.JsonObject { ["clientId"] = "winche-sync" },
        });
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = page, reason = "Staff" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/_console/api/server/secrets/server.consoleSignIn.staff.keycloakSync.clientSecret", new { value = "sync-secret" })).StatusCode);

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
            copied = await accounts.Services.GetRequiredService<Winche.Core.SecretStore>().GetAsync(Winche.Auth.Settings.KeycloakSyncSettings.SecretName("staff"));
            if (copied is null) await Task.Delay(100);
        }
        Assert.Equal("sync-secret", copied);

        // Taken off Server settings: off the accounts' providers too (its sync stops).
        page["consoleProviders"] = new System.Text.Json.Nodes.JsonArray();
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = page, reason = "No more staff" }));
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
        await FollowAsync(bob, idp, "/_console/api/auth/oidc/start");
        Assert.Equal("bob@example.com", await SignedInAsync(bob));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task A_project_with_nothing_started_answers_its_overview_auth_config_and_keys_without_reading_missing_tables()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));

        var overview = await Json(await admin.GetAsync("/_console/api/overview"));
        Assert.All(overview.GetProperty("products").EnumerateArray(), p => Assert.False(p.GetProperty("on").GetBoolean()));
        var config = await Json(await admin.GetAsync("/_console/api/app-auth/config"));
        Assert.Equal(0, config.GetProperty("keys").GetArrayLength());
        // No signing keys yet: refused like the rest of Authentication's API, not a server error.
        var jwks = await server.CreateClient().GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.BadRequest, jwks.StatusCode);
        Assert.Contains("service-disabled", await jwks.Content.ReadAsStringAsync());

        // Started: its keys are there.
        await StartProductAsync(admin, "", "authentication");
        Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().GetAsync("/.well-known/jwks.json")).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Console_accounts_are_a_winche_auth_of_their_own_apart_from_every_projects_app_users()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var db = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(db, files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));

        // An account in the console's Winche Auth signs in with its password, and gets tokens from the console's issuer.
        var accounts = server.Services.GetRequiredService<ConsoleAccounts>();
        await using (var scope = accounts.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<Winche.Auth.IWincheAuth>();
            var ann = await auth.CreateUserAsync("ann@clinic.example", "correct horse battery", "Ann");
            var tokens = await auth.SignInAsync("ann@clinic.example", "correct horse battery", Winche.Auth.AuthRequestInfo.Server);
            Assert.Equal(ann.Uid, tokens.User.Uid);
            Assert.Equal("http://localhost/_console/accounts", new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(tokens.AccessToken).Issuer);
            Assert.Equal(ann.Uid, (await auth.FindByEmailAsync("ann@clinic.example"))!.Uid);
        }

        // The project's app users are another Winche Auth: Ann isn't one of them, and can't sign in to the app.
        var refused = await server.CreateClient().PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@clinic.example", password = "correct horse battery" });
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
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));

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
        var started = await admin.PostAsync("/p/wards/_console/api/project/products/database/start", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = await Json(started);
        Assert.Contains(run.GetProperty("steps").EnumerateArray(), s => s.GetProperty("title").GetString() == "Create the document tables and functions");
        await StartProductAsync(admin, "/p/wards", "database");
        var tables = await Tables();
        Assert.Contains("winche_documents", tables);
        Assert.Contains("winche_hook_deliveries", tables);
        Assert.DoesNotContain("auth_users", tables);   // Auth isn't started
        Assert.DoesNotContain("winche_files", tables);
        var project = await Json(await admin.GetAsync("/p/wards/_console/api/project"));
        var database = project.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "database");
        Assert.True(database.GetProperty("ready").GetBoolean());
        Assert.DoesNotContain(project.GetProperty("products").EnumerateArray(), p => p.GetProperty("id").GetString() == "hooks");
        Assert.Equal(HttpStatusCode.NotFound, (await server.CreateClient().GetAsync("/p/wards/documents/v1/notes/a")).StatusCode);   // answered by the database itself
    }

    [Fact]
    public async Task The_first_project_gets_a_database_of_its_own_at_setup_and_moves_with_everything_kept()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        var control = await pg.NewDatabaseAsync();
        await using var server = new ServerFactory(control, files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        // Never the server's own database.
        var refused = await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>(),
            database = new { database = new NpgsqlConnectionStringBuilder(control).Database } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await Json(await admin.GetAsync("/_console/api/project"))).GetProperty("created").GetBoolean());

        // None named: a database of its own on this server's PostgreSQL.
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication", "database" } }));
        var app = server.CreateClient();
        var token = (await Json(await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }))).GetProperty("accessToken").GetString()!;
        var where = await Json(await admin.GetAsync("/_console/api/data-location"));
        var ownDb = where.GetProperty("database").GetString()!;
        Assert.EndsWith("_p_clinic", ownDb);
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(control) { Database = ownDb }.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM auth_users", c);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        // Nothing of the project in the server's database: what it logged before setup goes too.
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
        var check = await Json(await admin.PostAsJsonAsync("/_console/api/data-location/check", new { database = new NpgsqlConnectionStringBuilder(control).Database }));
        Assert.False(check.GetProperty("ok").GetBoolean());
        check = await Json(await admin.PostAsJsonAsync("/_console/api/data-location/check", new { database = newDb }));
        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/_console/api/data-location/move", new { database = newDb })).StatusCode);
        var sawPause = false;
        JsonElement after = default;
        for (var i = 0; i < 200; i++)
        {
            var r = await admin.GetAsync("/_console/api/data-location");
            if (r.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                Assert.Contains("project-moving", await r.Content.ReadAsStringAsync());
                sawPause = true;
            }
            else if (r.IsSuccessStatusCode && (after = await Json(r)).GetProperty("previous").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }
        Assert.True(sawPause);
        Assert.Equal(newDb, after.GetProperty("database").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastMoveFailed").ValueKind);

        // Everything carries on there: the user signs in, their earlier token still works for the API.
        await Json(await app.PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(control) { Database = newDb }.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM auth_users", c);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // Finishing deletes the old database.
        await admin.PostAsJsonAsync("/_console/api/data-location/finish", new { deleteOld = true });
        await using (var c = new NpgsqlConnection(control))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", c);
            cmd.Parameters.AddWithValue(ownDb);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.Equal(JsonValueKind.Null, (await Json(await admin.GetAsync("/_console/api/data-location"))).GetProperty("previous").ValueKind);
        await Json(await app.PostAsJsonAsync("/auth/v1/signIn", new { email = "ann@example.com", password = "correct horse battery" }));
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
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards" }));
        await StartProductAsync(admin, "/p/wards", "authentication");
        await Json(await server.CreateClient().PostAsJsonAsync("/p/wards/auth/v1/signUp", new { email = "bob@example.com", password = "correct horse battery" }));
        var oldDb = (await Json(await admin.GetAsync("/p/wards/_console/api/data-location"))).GetProperty("database").GetString()!;

        // "Another server": the test's PostgreSQL by address, user and password.
        var other = new NpgsqlConnectionStringBuilder(control);
        var target = new { database = "wards_elsewhere", host = other.Host, port = other.Port, username = other.Username, password = other.Password, sslMode = "Disable" };
        Assert.True((await Json(await admin.PostAsJsonAsync("/p/wards/_console/api/data-location/check", target))).GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/p/wards/_console/api/data-location/move", target)).StatusCode);
        JsonElement after = default;
        for (var i = 0; i < 200; i++)
        {
            var r = await admin.GetAsync("/p/wards/_console/api/data-location");
            if (r.IsSuccessStatusCode && (after = await Json(r)).GetProperty("previous").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }
        Assert.Equal(("wards_elsewhere", other.Host), (after.GetProperty("database").GetString(), after.GetProperty("host").GetString()));
        await Json(await server.CreateClient().PostAsJsonAsync("/p/wards/auth/v1/signIn", new { email = "bob@example.com", password = "correct horse battery" }));

        // Finished: the old database is dropped.
        await admin.PostAsJsonAsync("/p/wards/_console/api/data-location/finish", new { deleteOld = true });
        await using (var c = new NpgsqlConnection(control))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = $1", c) { Parameters = { new() { Value = oldDb } } };
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // Pool settings: saved, the project restarted with them, still answering.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/p/wards/_console/api/data-location/pool", new { maxConnections = 20, commandTimeoutSeconds = 60 })).StatusCode);
        Assert.Equal(20, (await Json(await admin.GetAsync("/p/wards/_console/api/data-location"))).GetProperty("maxConnections").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/p/wards/_console/api/data-location/pool", new { maxConnections = 1 })).StatusCode);
        await Task.Delay(300);
        await Json(await server.CreateClient().PostAsJsonAsync("/p/wards/auth/v1/signIn", new { email = "bob@example.com", password = "correct horse battery" }));
    }

    [Fact]
    public async Task Files_are_kept_on_disk_behind_signed_links_on_the_public_address()
    {
        // One folder for every project's files: the first project's too, in its own folder under it.
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), null, ("Winche:ProjectsPath", files));
        var app = server.CreateClient();
        var console = server.CreateClient();
        await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "database", "storage", "authentication", "functions", "hooks" } });
        var parsed = await Json(await console.PostAsJsonAsync("/_console/api/rules/text/parse", new { text = "match public/{file=**} { allow read, write: if request.auth != null; }" }));
        await Json(await console.PostAsJsonAsync("/_console/api/rules/storage", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Public files" }));

        var session = await Json(await app.PostAsJsonAsync("/auth/v1/signUp", new { email = "ben@example.com", password = "correct horse battery" }));
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var path = B64("public/hello.txt");
        await Json(await app.PutAsJsonAsync($"/files/v1/{path}", new { mimeType = "text/plain", sizeBytes = 5 }));
        var upload = (await Json(await app.PostAsync($"/files/v1/{path}:upload", null))).GetProperty("url").GetString()!;
        Assert.StartsWith("http://localhost/_files/", upload);
        var put = new HttpRequestMessage(HttpMethod.Put, new Uri(upload).PathAndQuery) { Content = new StringContent("hello") };
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(put)).StatusCode);
        await Json(await app.PostAsync($"/files/v1/{path}:confirm", null));
        // On disk in the project's folder, under its own key: the folder, a generation, the name.
        var stored = Assert.Single(Directory.GetFiles(files, "hello.txt", SearchOption.AllDirectories));
        Assert.Equal("hello", File.ReadAllText(stored));
        Assert.Matches($"^{Regex.Escape(files)}/[^/]+/files/objects/public/", stored);
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
        await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } });

        // The project's Authentication sends email to its app users; the console's invites don't use it.
        var signin = await Json(await console.GetAsync("/_console/api/app-auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Clinic", ["sendVerificationOnSignUp"] = true, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "smtp",
            ["smtp"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "127.0.0.1", ["port"] = smtp.Port, ["security"] = "none", ["username"] = "mailer", ["fromAddress"] = "no-reply@clinic.example" },
        };
        await Json(await console.PutAsJsonAsync("/_console/api/app-auth/settings/signin", new { settings, smtpPassword = "pw", reason = "Email" }));

        var invite = await Json(await console.PostAsJsonAsync("/_console/api/invites", new { email = "colleague@example.com", role = "Member", expiresInHours = 72 }));
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
        await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));

        // Server settings → Authentication → Settings → Emails to users: the console accounts' SMTP server, its password encrypted.
        var signin = await Json(await console.GetAsync("/_console/api/server/auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Winche", ["sendVerificationOnSignUp"] = false, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "smtp",
            ["smtp"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "127.0.0.1", ["port"] = smtp.Port, ["security"] = "none", ["username"] = "console-mailer", ["fromAddress"] = "console@winche.example", ["fromName"] = "Winche" },
        };
        await Json(await console.PutAsJsonAsync("/_console/api/server/auth/settings/signin", new { settings, smtpPassword = "pw", reason = "Console email" }));

        // Invites go through it.
        Assert.True((await Json(await console.PostAsJsonAsync("/_console/api/invites", new { email = "colleague@example.com", role = "Member", expiresInHours = 72 }))).GetProperty("emailed").GetBoolean());
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
        await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "authentication" } }));

        // Console invites go out with nothing set up: no SMTP server, no keys saved.
        Assert.True((await Json(await console.PostAsJsonAsync("/_console/api/invites", new { email = "colleague@example.com", role = "Member", expiresInHours = 72 }))).GetProperty("emailed").GetBoolean());
        var invite = Assert.Single(ses.Sent);
        Assert.Equal("colleague@example.com", invite["Destination"]!["ToAddresses"]![0]!.GetValue<string>());
        Assert.Equal("Winche console <no-reply@winche.example>", invite["FromEmailAddress"]!.GetValue<string>());
        Assert.Contains("/invite?token=", invite["Content"]!["Simple"]!["Body"]!["Text"]!["Data"]!.GetValue<string>());
        Assert.Contains("Credential=AKIDTESTONLY/", ses.Authorizations[0]);   // signed with the environment's credentials

        // A project's sign-in emails: the console offers the server's email, and sending through it needs no setup.
        var config = await Json(await console.GetAsync("/_console/api/app-auth/config"));
        Assert.Equal("Amazon SES (eu-west-1) as no-reply@winche.example", config.GetProperty("serverEmail").GetString());
        var signin = await Json(await console.GetAsync("/_console/api/app-auth/settings/signin"));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(signin.GetProperty("settings").GetRawText())!;
        settings["email"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true, ["appName"] = "Clinic", ["sendVerificationOnSignUp"] = false, ["maxPerAddressPerHour"] = 5, ["verifyLinkHours"] = 24,
            ["resetLinkHours"] = 1, ["sender"] = "app",
        };
        await Json(await console.PutAsJsonAsync("/_console/api/app-auth/settings/signin", new { settings, reason = "Email through the server" }));
        var user = await Json(await console.PostAsJsonAsync("/_console/api/app-auth/users", new { email = "ann@example.com", password = "Ann-Passw0rd!" }));
        Assert.True((await console.PostAsync($"/_console/api/app-auth/users/{user.GetProperty("uid").GetString()}/sendPasswordReset", null)).IsSuccessStatusCode);
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
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await admin.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards", products = new[] { "storage" } }));
        await Json(await admin.PostAsync("/p/wards/_console/api/project/products/storage/start", null));
        for (var i = 0; i < 100 && !(await Json(await admin.GetAsync("/p/wards/_console/api/project"))).GetProperty("products").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "storage" && p.GetProperty("ready").GetBoolean()); i++)
            await Task.Delay(100);

        // Bob is the project's admin, not the server's.
        var added = await Json(await admin.PostAsJsonAsync("/p/wards/_console/api/members", new { email = "bob@example.com", role = "Admin" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(added.GetProperty("link").GetString()!).Query)["code"];
        await admin.PostAsJsonAsync("/p/wards/_console/api/auth/reset-password", new { code, newPassword = "Bob-Passw0rd!long" });
        var bob = server.CreateClient();
        await Json(await bob.PostAsJsonAsync("/p/wards/_console/api/auth/login", new { email = "bob@example.com", password = "Bob-Passw0rd!long" }));

        var keyless = new { kind = "s3", s3 = new { bucket = "server-bucket", region = "eu-west-1" } };
        var refused = await bob.PostAsJsonAsync("/p/wards/_console/api/settings/storage/location/test", new { location = keyless });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("only a server admin", await refused.Content.ReadAsStringAsync());
        refused = await bob.PostAsJsonAsync("/p/wards/_console/api/settings/storage/location/move", new { location = keyless, reason = "Mine now" });
        Assert.Contains("only a server admin", await refused.Content.ReadAsStringAsync());

        // With a key of its own, his test is run (and fails here: there's no such bucket), not refused.
        var keyed = await bob.PostAsJsonAsync("/p/wards/_console/api/settings/storage/location/test",
            new { location = new { kind = "s3", s3 = new { bucket = "wards-bucket", region = "eu-west-1", serviceUrl = "http://127.0.0.1:9", accessKey = "AKIDWARDS", forcePathStyle = true } }, secretKey = "s" });
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        // The server admin may use the server's own credentials.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/p/wards/_console/api/settings/storage/location/test",
            new { location = new { kind = "s3", s3 = new { bucket = "server-bucket", region = "eu-west-1", serviceUrl = "http://127.0.0.1:9", forcePathStyle = true } } })).StatusCode);
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task An_app_user_disabled_or_deleted_loses_their_open_database_connection_at_once()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "database", "authentication" } }));
        var app = server.CreateClient();

        async Task<(string Uid, System.Net.WebSockets.WebSocket Socket)> Connect(string email)
        {
            var session = await Json(await app.PostAsJsonAsync("/auth/v1/signUp", new { email, password = "correct horse battery" }));
            var token = session.GetProperty("accessToken").GetString()!;
            var ws = server.Server.CreateWebSocketClient();
            var socket = await ws.ConnectAsync(new Uri(server.Server.BaseAddress, $"/documents/v1/ws?access_token={token}"), CancellationToken.None);
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
        await Json(await console.PostAsJsonAsync($"/_console/api/app-auth/users/{ann}/disabled", new { disabled = true }));
        Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4401, await ClosedWith(annSocket));

        var (bob, bobSocket) = await Connect("bob@example.com");
        Assert.True((await console.DeleteAsync($"/_console/api/app-auth/users/{bob}")).IsSuccessStatusCode);
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
        await Json(await console.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = Array.Empty<string>() }));
        await Json(await console.PostAsJsonAsync("/_console/api/projects", new { name = "Wards", id = "wards", products = new[] { "functions" } }));
        await Json(await console.PostAsync("/p/wards/_console/api/project/products/functions/start", null));
        for (var i = 0; i < 100 && !(await Json(await console.GetAsync("/p/wards/_console/api/project"))).GetProperty("products").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "functions" && p.GetProperty("ready").GetBoolean()); i++)
            await Task.Delay(100);
        await Json(await console.PostAsJsonAsync("/p/wards/_console/api/functions/deploy", new { note = "Hook", files = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { http } from "@winche/functions";
                export const hook = http.onRequest(async (req) => ({ method: req.method, path: new URL(req.url).pathname, body: await req.text() }));
                """,
        } }));
        var anyone = server.CreateClient();
        var answer = await Json(await anyone.PostAsync("/p/wards/functions/v1/http/hook/x", new StringContent("hello")));
        Assert.Equal(("POST", "/p/wards/functions/v1/http/hook/x", "hello"),
            (answer.GetProperty("method").GetString(), answer.GetProperty("path").GetString(), answer.GetProperty("body").GetString()));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task How_long_functions_run_is_the_servers_longest_then_the_projects_default_and_each_functions_own()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // functions need Deno
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/_console/api/project", new { name = "Clinic", id = "clinic", products = new[] { "functions" } }));
        await Json(await admin.PostAsJsonAsync("/_console/api/functions/deploy", new { note = "Jobs", files = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, schedule } from "@winche/functions";
                export const quick = callable(async () => 1);
                export const backfill = schedule("0 3 * * *", async () => {}, { timeoutSeconds: 480 });
                """,
        } }));
        async Task<(int Max, int Quick, int Backfill)> Times()
        {
            var page = await Json(await admin.GetAsync("/_console/api/functions"));
            int T(string n) => page.GetProperty("current").GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == n).GetProperty("timeoutSeconds").GetInt32();
            return (page.GetProperty("maxTimeoutSeconds").GetInt32(), T("quick"), T("backfill"));
        }
        Assert.Equal((540, 60, 480), await Times());

        // The project: a longer default, and the backfill's own time, up to the server's longest.
        var settings = System.Text.Json.Nodes.JsonNode.Parse("""{ "defaultTimeoutSeconds": 120, "timeouts": { "backfill": 1800 } }""")!;
        var refused = await admin.PutAsJsonAsync("/_console/api/settings/functions", new { settings, reason = "Longer backfill" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("backfill: 1 second to 9 minutes", await refused.Content.ReadAsStringAsync());

        // The server allows longer runs: in force at once, with no restart.
        var page = await Json(await admin.GetAsync("/_console/api/server/settings"));
        var server1 = System.Text.Json.Nodes.JsonNode.Parse(page.GetProperty("settings").GetRawText())!.AsObject();
        server1["functionsMaxMinutes"] = 61;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = server1, reason = "x" })).StatusCode);
        server1["functionsMaxMinutes"] = 45;
        await Json(await admin.PutAsJsonAsync("/_console/api/server/settings", new { settings = server1, reason = "Long backfills" }));
        for (var i = 0; i < 100 && (await Times()).Max != 2700; i++) await Task.Delay(100);
        await Json(await admin.PutAsJsonAsync("/_console/api/settings/functions", new { settings, reason = "Longer backfill" }));
        for (var i = 0; i < 100 && (await Times()).Quick != 120; i++) await Task.Delay(100);
        Assert.Equal((2700, 120, 1800), await Times());
        Directory.Delete(files, recursive: true);
    }
}
