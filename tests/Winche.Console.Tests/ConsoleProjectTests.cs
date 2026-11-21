using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Winche.Console.Tests;

[Collection("postgres")]
public class ConsoleProjectTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fx.ResetAuthAsync();
        await fx.ResetRulesAsync();   // published rules would make the console look "already in use" (carried over)
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            DO $$ BEGIN
              IF to_regclass('winche_settings') IS NOT NULL THEN TRUNCATE winche_settings; END IF;
              IF to_regclass('winche_integrity_usage') IS NOT NULL THEN TRUNCATE winche_integrity_usage; END IF;
              IF to_regclass('winche_console_project_image') IS NOT NULL THEN TRUNCATE winche_console_project_image; END IF;
            END $$;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static bool Started(JsonElement project, string product) =>
        project.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetString() == product).GetProperty("started").GetBoolean();

    [Fact]
    public async Task Products_are_off_until_the_project_is_created_and_each_is_started()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();

        var project = await Json(await client.GetAsync("/console/v1/project"));
        Assert.True(project.GetProperty("setup").GetBoolean());
        Assert.False(project.GetProperty("created").GetBoolean());

        // Not started: the public APIs refuse, and /.well-known/winche lists nothing.
        var refused = await client.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("service-disabled", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, (await Json(await client.GetAsync("/.well-known/winche"))).GetProperty("services").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/project", new { name = "Clinic", id = "Bad Id", products = Array.Empty<string>() })).StatusCode);
        project = await Json(await client.PostAsJsonAsync("/console/v1/project", new { name = "Clinic records", id = "clinic-records", products = new[] { "authentication" } }));
        Assert.True(project.GetProperty("created").GetBoolean());
        Assert.True(Started(project, "authentication"));
        Assert.False(Started(project, "functions"));
        Assert.Equal("Clinic records", (await Json(await client.GetAsync("/console/v1/session/config"))).GetProperty("project").GetProperty("name").GetString());

        // Authentication runs; functions still refuse until started.
        await Json(await client.PostAsJsonAsync("/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" }));
        Assert.Contains("service-disabled", await (await client.PostAsJsonAsync("/functions/v1/call/hello", new { data = 1 })).Content.ReadAsStringAsync());
        // Starting runs its setup in the background; it's started once that's done.
        await Json(await client.PostAsync("/console/v1/project/products/functions/start", null));
        for (var i = 0; i < 100 && (await Json(await client.GetAsync("/console/v1/project/products/functions/setup"))).GetProperty("state").GetString() != "done"; i++)
            await Task.Delay(50);
        project = await Json(await client.GetAsync("/console/v1/project"));
        Assert.True(Started(project, "functions"));
        Assert.DoesNotContain("service-disabled", await (await client.PostAsJsonAsync("/functions/v1/call/hello", new { data = 1 })).Content.ReadAsStringAsync());

        var wellKnown = await Json(await client.GetAsync("/.well-known/winche"));
        Assert.Equal("clinic-records", wellKnown.GetProperty("projectId").GetString());
        Assert.Equal(["auth", "functions"], wellKnown.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetString()).Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/project", new { name = "Again", id = "again" })).StatusCode);
    }

    [Fact]
    public async Task Without_a_multi_project_host_the_console_knows_only_its_own_project()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        var projects = await Json(await client.GetAsync("/console/v1/projects"));
        Assert.False(projects.GetProperty("available").GetBoolean());
        Assert.Equal(0, projects.GetProperty("projects").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/projects", new { name = "Wards", id = "wards" })).StatusCode);
    }

    [Fact]
    public async Task Integrity_apps_are_named_in_their_options_debug_tokens_get_tokens_counted_per_product_and_enforced()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        await Json(await client.PostAsJsonAsync("/console/v1/project", new { name = "Clinic records", id = "clinic-records", products = new[] { "authentication" } }));

        var empty = await Json(await client.GetAsync("/console/v1/integrity"));
        Assert.True(empty.GetProperty("available").GetBoolean());
        Assert.True(empty.GetProperty("canSign").GetBoolean());
        Assert.Equal(0, empty.GetProperty("apps").GetArrayLength());
        Assert.Equal("monitor", empty.GetProperty("enforcement").GetProperty("authentication").GetString());
        // Enforcing needs an app first: nothing could call.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/console/v1/integrity/enforcement",
            new { enforcement = new Dictionary<string, string> { ["authentication"] = "enforce" }, reason = "Lock it" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "Clinic", androidPackage = "not a package" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "Clinic", appleTeamId = "short" })).StatusCode);
        var id = (await Json(await client.PostAsJsonAsync("/console/v1/integrity/apps",
            new { name = "Clinic app", androidPackage = "com.example.clinic", appleBundleId = "com.example.clinic", appleTeamId = "abcde12345" }))).GetProperty("id").GetString()!;
        Assert.Matches("^app-[a-z2-9]{12}$", id);
        Assert.Equal("ABCDE12345", (await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("apps")[0].GetProperty("appleTeamId").GetString());

        // An id can be chosen when adding: valid, and not taken. Editing never changes it.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "Bad", id = "Not An Id" })).StatusCode);
        Assert.Equal("clinic-web", (await Json(await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "Clinic web", id = "clinic-web" }))).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "Again", id = "clinic-web" })).StatusCode);
        await Json(await client.PutAsJsonAsync("/console/v1/integrity/apps/clinic-web", new { name = "Clinic web app", id = "renamed" }));
        Assert.Contains((await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("apps").EnumerateArray(),
            a => a.GetProperty("id").GetString() == "clinic-web" && a.GetProperty("name").GetString() == "Clinic web app");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/console/v1/integrity/apps/clinic-web")).StatusCode);

        // Its options file and /.well-known/winche?app= name it; an unknown id is a 404.
        Assert.Contains($"appId: '{id}'", (await Json(await client.GetAsync($"/console/v1/apps?platforms=android&app={id}"))).GetProperty("file").GetString());
        Assert.Equal(id, (await Json(await client.GetAsync($"/.well-known/winche?app={id}"))).GetProperty("app").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/winche?app=app-nope")).StatusCode);

        // A debug token, shown once, is traded for an Integrity token.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/console/v1/integrity/debug-tokens", new { app = id, name = "" })).StatusCode);
        var made = await Json(await client.PostAsJsonAsync("/console/v1/integrity/debug-tokens", new { app = id, name = "CI" }));
        var debugToken = made.GetProperty("token").GetString()!;
        var listedTokens = (await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("debugTokens");
        Assert.Equal("CI", listedTokens[0].GetProperty("name").GetString());
        Assert.DoesNotContain(debugToken, listedTokens.ToString());   // never shown again
        var exchanged = await Json(await client.PostAsJsonAsync("/integrity/v1/exchange", new { app = id, provider = "debug", debugToken }));
        var token = exchanged.GetProperty("token").GetString()!;

        // Requests are counted per app and product (those without a token apart).
        var email = $"ann-{Guid.NewGuid():N}@example.com";
        var signUp = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/signUp") { Content = JsonContent.Create(new { email = email, password = "correct horse battery" }) };
        signUp.Headers.Add("X-Winche-Integrity", token);
        await Json(await client.SendAsync(signUp));
        await client.PostAsJsonAsync("/auth/v1/signIn", new { email = email, password = "correct horse battery" });
        var listed = await Json(await client.GetAsync("/console/v1/integrity"));
        Assert.Equal(1, listed.GetProperty("apps")[0].GetProperty("usage").GetProperty("authentication").GetProperty("today").GetInt64());
        Assert.Equal(1, listed.GetProperty("unverified").GetProperty("authentication").GetProperty("today").GetInt64());

        // Enforced: without a token, refused; with one (header, or the WebSocket query parameter), answered.
        await Json(await client.PutAsJsonAsync("/console/v1/integrity/enforcement",
            new { enforcement = new Dictionary<string, string> { ["authentication"] = "enforce" }, reason = "Only our apps" }));
        var refused = await client.PostAsJsonAsync("/auth/v1/signIn", new { email = email, password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("integrity-required", await refused.Content.ReadAsStringAsync());
        var verified = new HttpRequestMessage(HttpMethod.Post, $"/auth/v1/signIn?winche_integrity={Uri.EscapeDataString(token)}") { Content = JsonContent.Create(new { email = email, password = "correct horse battery" }) };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(verified)).StatusCode);
        // Links opened from emails never need one.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/auth/v1/action?mode=verifyEmail&code=x")).StatusCode);

        // Removing the debug token ends its tokens; removing the last app puts enforced products back to monitor.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/console/v1/integrity/debug-tokens/{made.GetProperty("id").GetString()}")).StatusCode);
        var again = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/signIn") { Content = JsonContent.Create(new { email = email, password = "correct horse battery" }) };
        again.Headers.Add("X-Winche-Integrity", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(again)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/console/v1/integrity/apps/{id}")).StatusCode);
        Assert.Equal("monitor", (await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("enforcement").GetProperty("authentication").GetString());
    }

    [Fact]
    public async Task Apps_registered_before_Integrity_carry_over_until_its_settings_are_saved()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        await Json(await client.PostAsJsonAsync("/console/v1/project", new { name = "Clinic records", id = "clinic-records", products = new[] { "authentication" } }));
        // As the console kept them before (area "apps", with fields Integrity doesn't have).
        var store = app.Services.GetRequiredService<Winche.Core.SettingsStore>();
        await store.SaveAsync("apps", System.Text.Json.Nodes.JsonNode.Parse("""
            { "apps": [ { "id": "app-old", "name": "Old app", "sdk": "flutter", "androidPackage": "com.example.old", "webOrigins": ["https://old.example.com"] } ],
              "registeredOnly": true, "browserOrigins": ["https://x.example.com"] }
            """)!, "admin@example.com", "Before Integrity");

        var carried = Assert.Single((await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("apps").EnumerateArray());
        Assert.Equal(("app-old", "com.example.old"), (carried.GetProperty("id").GetString(), carried.GetProperty("androidPackage").GetString()));
        Assert.Equal("monitor", (await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("enforcement").GetProperty("authentication").GetString());   // "registered only" isn't enforcement
        // The first save keeps them, in Integrity's own settings.
        await Json(await client.PostAsJsonAsync("/console/v1/integrity/apps", new { name = "New app" }));
        Assert.Equal(2, (await Json(await client.GetAsync("/console/v1/integrity"))).GetProperty("apps").GetArrayLength());
    }

    [Fact]
    public async Task The_project_is_renamed_keeping_its_id_and_takes_a_thumbnail()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        await Json(await client.PostAsJsonAsync("/console/v1/project", new { name = "Clinic records", id = "clinic-records", products = Array.Empty<string>() }));

        var renamed = await Json(await client.PutAsJsonAsync("/console/v1/project", new { name = "Clinic", id = "clinic-records" }));
        Assert.Equal("Clinic", renamed.GetProperty("name").GetString());
        Assert.Equal("clinic-records", renamed.GetProperty("id").GetString());

        var project = await Json(await client.GetAsync("/console/v1/project"));
        Assert.Equal(JsonValueKind.Null, project.GetProperty("imageVersion").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/console/v1/project/image")).StatusCode);

        // Only PNG, JPEG or WebP, judged by the bytes: an SVG (which can carry scripts) is refused whatever it claims.
        var svg = new ByteArrayContent("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray());
        svg.Headers.ContentType = new("image/png");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/console/v1/project/image", svg)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/console/v1/project/image", new ByteArrayContent(new byte[300 * 1024]))).StatusCode);

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        await Json(await client.PutAsync("/console/v1/project/image", new ByteArrayContent(png)));
        project = await Json(await client.GetAsync("/console/v1/project"));
        Assert.False(string.IsNullOrEmpty(project.GetProperty("imageVersion").GetString()));
        var image = await client.GetAsync($"/console/v1/project/image?v={project.GetProperty("imageVersion").GetString()}");
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", image.Headers.CacheControl?.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/console/v1/project/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/console/v1/project/image")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await client.GetAsync("/console/v1/project"))).GetProperty("imageVersion").ValueKind);
    }

    [Fact]
    public async Task A_console_already_in_use_keeps_every_product_on()
    {
        using (var before = new ConsoleAppFactory(fx))
        {
            using var c = before.CreateClient();
            await c.SetupAdminAsync();   // in use before setup existed
        }
        using var app = new ConsoleAppFactory(fx, ("Sample:ProjectSetup", "true"));
        using var client = app.CreateClient();
        await client.LoginAsync();
        var project = await Json(await client.GetAsync("/console/v1/project"));
        Assert.True(project.GetProperty("created").GetBoolean());
        Assert.All(project.GetProperty("products").EnumerateArray(), p => Assert.True(p.GetProperty("started").GetBoolean()));
    }
}
