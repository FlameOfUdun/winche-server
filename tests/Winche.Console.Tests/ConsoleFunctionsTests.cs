using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Winche.Console.Tests;

/// <summary>The Functions page: deploy, versions, run, deploy key. Deploying needs Deno (WINCHE_DENO).</summary>
[Collection("postgres")]
public class ConsoleFunctionsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fx.ResetAuthAsync();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            DO $$ BEGIN
              IF to_regclass('winche_settings') IS NOT NULL THEN TRUNCATE winche_settings; END IF;
              IF to_regclass('winche_secrets') IS NOT NULL THEN TRUNCATE winche_secrets; END IF;
              IF to_regclass('winche_function_deployments') IS NOT NULL THEN TRUNCATE winche_function_deployments; END IF;
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

    [Fact]
    public async Task Functions_are_deployed_run_and_rolled_back_from_the_console()
    {
        using var app = new ConsoleAppFactory(fx);
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        var state = await Json(await client.GetAsync("/console/v1/session/state"));
        Assert.True(state.GetProperty("capabilities").GetProperty("functions").GetBoolean());
        var page = await Json(await client.GetAsync("/console/v1/functions"));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("current").ValueKind);
        // Deno comes from WINCHE_DENO, else PATH. Set WINCHE_DENO to require it; without Deno, it's said plainly.
        var problem = page.GetProperty("runtimeProblem");
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is { Length: > 0 })
            Assert.Equal(JsonValueKind.Null, problem.ValueKind);
        else if (problem.ValueKind == JsonValueKind.String)
        {
            Assert.Contains("Deno", problem.GetString());
            return;
        }

        var refused = await client.PostAsJsonAsync("/console/v1/functions/deploy", new { files = new Dictionary<string, string> { ["index.ts"] = "export const = ;" }, note = "Broken" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("doesn't build", await refused.Content.ReadAsStringAsync());

        string Code(string greeting) => $$"""
            import { callable } from "@winche/functions"; import * as admin from "@winche/admin";
            export const hello = callable(async (data: { name: string }, ctx) => {
              const n = await admin.documents.collection("people").count();
              console.log("people:", n);
              return `{{greeting}} ${data.name} (${ctx.caller?.uid ?? "server"})`;
            });
            """;
        var v1 = await Json(await client.PostAsJsonAsync("/console/v1/functions/deploy", new { files = new Dictionary<string, string> { ["index.ts"] = Code("Hello") }, note = "First" }));
        Assert.Equal((1, "hello"), (v1.GetProperty("version").GetInt32(), v1.GetProperty("functions")[0].GetProperty("name").GetString()));
        await Json(await client.PostAsJsonAsync("/console/v1/functions/deploy", new { files = new Dictionary<string, string> { ["index.ts"] = Code("Hi") }, note = "Second" }));

        var run = await Json(await client.PostAsJsonAsync("/console/v1/functions/run", new { name = "hello", data = new { name = "Ann" }, asUid = "u1" }));
        Assert.Equal(("Hi Ann (u1)", "people: 0"), (run.GetProperty("result").GetString(), run.GetProperty("logs")[0].GetProperty("message").GetString()));

        await Json(await client.PostAsJsonAsync("/console/v1/functions/versions/1/rollback", new { note = (string?)null }));
        run = await Json(await client.PostAsJsonAsync("/console/v1/functions/run", new { name = "hello", data = new { name = "Ann" } }));
        Assert.Equal("Hello Ann (server)", run.GetProperty("result").GetString());
        var versions = await Json(await client.GetAsync("/console/v1/functions/versions"));
        Assert.Equal([3, 2, 1], versions.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));

        // CI: a token with the functions scope deploys (named in the versions); it can't run functions or do anything else.
        var made = await Json(await client.PostAsJsonAsync("/console/v1/tokens", new { name = "gitlab-main", scopes = new[] { "functions" }, days = 30 }));
        using var ci = app.CreateClient();
        ci.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", made.GetProperty("secret").GetString());
        var deployed = await Json(await ci.PostAsJsonAsync("/console/v1/functions/deploy", new { files = new Dictionary<string, string> { ["index.ts"] = Code("Hey") }, note = "From CI" }));
        Assert.Equal((4, "gitlab-main (CI token by admin@example.com)"), (deployed.GetProperty("version").GetInt32(), deployed.GetProperty("by").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.PostAsJsonAsync("/console/v1/functions/run", new { name = "hello" })).StatusCode);
        // Deploy keys are gone (nothing answers there: the SPA's fallback only serves GET).
        Assert.Contains((await client.PostAsync("/console/v1/functions/deploy-key", null)).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains((await ci.PostAsJsonAsync("/functions/v1/deploy", new { files = new Dictionary<string, string>() })).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
    [Fact]
    public async Task Secrets_are_set_listed_without_values_and_removed()
    {
        using var app = new ConsoleAppFactory(fx);
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // deploying needs Deno

        await Json(await client.PostAsJsonAsync("/console/v1/functions/deploy", new { files = new Dictionary<string, string> { ["index.ts"] = """
            import { callable, secrets } from "@winche/functions";
            const mailKey = secrets.define("MAIL_KEY");
            export const keyLength = callable(async () => mailKey.value().length, { secrets: [mailKey] });
            """ }, note = "Secrets" }));
        var list = await Json(await client.GetAsync("/console/v1/functions/secrets"));
        Assert.True(list.GetProperty("available").GetBoolean());
        var only = Assert.Single(list.GetProperty("secrets").EnumerateArray());
        Assert.Equal(("MAIL_KEY", false, "keyLength"), (only.GetProperty("name").GetString(), only.GetProperty("isSet").GetBoolean(), only.GetProperty("usedBy")[0].GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/console/v1/functions/secrets/mail-key", new { value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/console/v1/functions/secrets/MAIL_KEY", new { value = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/console/v1/functions/secrets/MAIL_KEY", new { value = "k-123456" })).StatusCode);

        var text = await (await client.GetAsync("/console/v1/functions/secrets")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("k-123456", text);   // values never come back
        Assert.Contains("\"isSet\":true", text);
        var run = await Json(await client.PostAsJsonAsync("/console/v1/functions/run", new { name = "keyLength" }));
        Assert.Equal(8, run.GetProperty("result").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/console/v1/functions/secrets/MAIL_KEY")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/console/v1/functions/secrets/MAIL_KEY")).StatusCode);
    }

    [Fact]
    public async Task Console_pages_are_listed_by_role_served_sandboxed_and_the_admin_api_checks_the_role()
    {
        using var app = new ConsoleAppFactory(fx);
        using var admin = app.CreateClient();
        await admin.SetupAdminAsync();
        await admin.LoginAsync();
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // deploying needs Deno

        await Json(await admin.PostAsJsonAsync("/console/v1/functions/deploy", new { note = "Pages", files = new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { pages, callable } from "@winche/functions";
                export const reports = pages({ title: "Reports", files: "console/reports", icon: "chart-bar" });
                export const tools = pages({ title: "Tools", files: "console/tools", minRole: "admin" });
                export const whoami = callable(async (_d, ctx) => ctx.caller?.uid ?? "nobody");
                """,
            ["console/reports/index.html"] = "<!doctype html><html><head><title>R</title></head><body><script src=\"app.js\"></script></body></html>",
            ["console/reports/app.js"] = "console.log('reports')",
            ["console/reports/sub/more.html"] = "<html><head></head><body>more</body></html>",
            ["console/tools/index.html"] = "<p>tools</p>",
        } }));
        var state = await Json(await admin.GetAsync("/console/v1/session/state"));
        Assert.True(state.GetProperty("capabilities").GetProperty("consolePages").GetBoolean());
        var pages = await Json(await admin.GetAsync("/console/v1/pages"));
        Assert.Equal(["reports", "tools"], pages.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "v@example.com", role = "Viewer", password = "Passw0rd!" });
        using var viewer = app.CreateClient();
        await viewer.LoginAsync("v@example.com", "Passw0rd!");
        pages = await Json(await viewer.GetAsync("/console/v1/pages"));
        Assert.Equal(["reports"], pages.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync("/console/v1/pages/tools/open", null)).StatusCode);

        var opened = await Json(await viewer.PostAsync("/console/v1/pages/reports/open", null));
        var url = opened.GetProperty("url").GetString()!;
        Assert.Equal(("v@example.com", "viewer"), (opened.GetProperty("user").GetProperty("email").GetString(), opened.GetProperty("user").GetProperty("role").GetString()));

        // The frame's requests carry no session: the signed address is enough, and the page is sandboxed.
        using var frame = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var index = await frame.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var csp = index.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("sandbox allow-scripts", csp);
        Assert.Contains("connect-src 'none'", csp);
        var html = await index.Content.ReadAsStringAsync();
        // @winche/admin resolves to the SDK served beside the page; the bridge carries its calls to the console.
        Assert.Contains("<head><script type=\"importmap\">{\"imports\":{\"@winche/admin\":\"_winche/admin.js\"}}</script><script src=\"_winche/console.js\"></script><title>R</title>", html);
        var sub = await frame.GetStringAsync(url + "sub/more.html");
        Assert.Contains("\"@winche/admin\":\"../_winche/admin.js\"", sub);
        Assert.Contains("../_winche/console.js", sub);
        Assert.Contains("Symbol.for(\"winche.page\")", await frame.GetStringAsync(url + "_winche/console.js"));
        Assert.Contains("initializeAdmin", await frame.GetStringAsync(url + "_winche/admin.js"));
        Assert.Equal("text/javascript; charset=utf-8", (await frame.GetAsync(url + "app.js")).Content.Headers.ContentType!.ToString());
        // A sandboxed frame's origin is "null": its module scripts (type="module", import) load in CORS mode.
        using (var module = new HttpRequestMessage(HttpMethod.Get, url + "app.js") { Headers = { { "Origin", "null" } } })
            Assert.Equal("*", (await frame.SendAsync(module)).Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False((await frame.GetAsync(url + "../tools/index.html")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await frame.GetAsync(url + "..%2F..%2Ftools%2Findex.html")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await frame.GetAsync(url.Replace("/view/", "/view/x"))).StatusCode);   // a tampered link
        Assert.Equal(HttpStatusCode.NotFound, (await frame.GetAsync(url.Replace("/reports/", "/tools/"))).StatusCode);   // a link is for one page
        // @winche/admin from a page (relayed by the console) or another server: the caller's role decides.
        Assert.Equal(HttpStatusCode.Unauthorized, (await frame.PostAsJsonAsync("/console/v1/admin", new { op = "db.get", args = new { path = "notes/a" } })).StatusCode);
        await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "m@example.com", role = "Member", password = "Passw0rd!" });
        using var member = app.CreateClient();
        await member.LoginAsync("m@example.com", "Passw0rd!");
        static object Add(string text) => new { op = "db.add", args = new { collection = "notes", fields = new { text = new { stringValue = text } } } };

        var denied = await viewer.PostAsJsonAsync("/console/v1/admin", Add("by viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("db.add needs the member role in this project (yours: viewer)", await denied.Content.ReadAsStringAsync());
        var added = await Json(await member.PostAsJsonAsync("/console/v1/admin", Add("by member")));
        var path = added.GetProperty("result").GetProperty("document").GetProperty("path").GetString()!;
        var read = await Json(await viewer.PostAsJsonAsync("/console/v1/admin", new { op = "db.get", args = new { path } }));
        Assert.Equal("by member", read.GetProperty("result").GetProperty("document").GetProperty("fields").GetProperty("text").GetProperty("stringValue").GetString());
        var history = await Json(await admin.GetAsync($"/console/v1/documents/history?path={path}"));
        Assert.Equal("console:m@example.com", history.GetProperty("entries")[0].GetProperty("actor").GetString());   // the member's write

        // Calling functions is an admin's, and never a page's (callables are the apps').
        static object Call() => new { op = "functions.call", args = new { name = "whoami" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/console/v1/admin", Call())).StatusCode);
        Assert.Equal("nobody", (await Json(await admin.PostAsJsonAsync("/console/v1/admin", Call()))).GetProperty("result").GetString());
        using (var fromPage = new HttpRequestMessage(HttpMethod.Post, "/console/v1/admin") { Content = JsonContent.Create(Call()), Headers = { { "X-Winche-Page", "reports" } } })
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(fromPage)).StatusCode);
        var missing = await viewer.PostAsJsonAsync("/console/v1/admin", new { op = "db.get", args = new { path = "notes" } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);   // the binding's own refusal, with its code
    }
}
