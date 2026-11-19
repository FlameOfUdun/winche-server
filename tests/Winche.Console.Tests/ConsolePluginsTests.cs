using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Winche.Console.Tests;

/// <summary>Functions → Plugins: installing, configuring, upgrading and removing plugin copies. Needs Deno (WINCHE_DENO).</summary>
[Collection("postgres")]
public class ConsolePluginsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fx.ResetAuthAsync();
        await ClearAsync();
    }

    /// <summary>No deployment left for the next test's app to start the functions runtime for (its log line would be counted).</summary>
    private async Task ClearAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            DO $$ BEGIN
              IF to_regclass('winche_secrets') IS NOT NULL THEN TRUNCATE winche_secrets; END IF;
              IF to_regclass('winche_function_deployments') IS NOT NULL THEN TRUNCATE winche_function_deployments; END IF;
            END $$;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => ClearAsync();

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Plugin(string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "winche-console-plugins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""{ "name": "greeter-plugin", "version": "{{version}}", "type": "module", "main": "./index.js" }""");
        File.WriteAllText(Path.Combine(dir, "winche-plugin.json"), """
            { "title": "Greeter", "settings": { "GREETING": { "label": "Greeting", "required": true } },
              "secrets": { "API_KEY": { "label": "API key" } }, "access": ["Calls nothing; says hello."] }
            """);
        File.WriteAllText(Path.Combine(dir, "README.md"), "# Greeter");
        File.WriteAllText(Path.Combine(dir, "index.js"), $$"""
            import { callable, plugin } from "@winche-technologies/winche-functions";
            export default plugin((p) => ({ greet: callable(async (d) => `${p.setting("GREETING")} ${d.name} ({{version}})`) }));
            """);
        return dir;
    }

    [Fact]
    public async Task Plugins_are_installed_configured_upgraded_and_removed_as_functions_versions()
    {
        using var app = new ConsoleAppFactory(fx);
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 }) return;   // deploying needs Deno
        var options = app.Services.GetRequiredService<Winche.Functions.FunctionsOptions>();
        options.ProvidedPlugins["greeter-plugin"] = Plugin("1.0.0");

        options.PluginRegistry = "http://127.0.0.1:9/";   // nothing there: no npm in tests
        options.PluginCatalog = ["greeter-plugin", "not-a-plugin"];
        var catalog = (await Json(await client.GetAsync("/console/v1/functions/plugins/catalog"))).GetProperty("plugins");
        Assert.Equal(("Greeter", "1.0.0"), (catalog[0].GetProperty("title").GetString(), catalog[0].GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.String, catalog[1].GetProperty("problem").ValueKind);   // listed, but npm (or the server) can't give it
        var package = await Json(await client.GetAsync("/console/v1/functions/plugins/package?name=greeter-plugin"));
        Assert.Equal(("1.0.0", "Greeter", "GREETING"), (package.GetProperty("version").GetString(), package.GetProperty("manifest").GetProperty("title").GetString(),
            package.GetProperty("manifest").GetProperty("settings")[0].GetProperty("name").GetString()));

        var refused = await client.PostAsJsonAsync("/console/v1/functions/plugins", new { id = "hello", package = "greeter-plugin" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Greeting (GREETING) is needed", await refused.Content.ReadAsStringAsync());
        var installed = await Json(await client.PostAsJsonAsync("/console/v1/functions/plugins", new { id = "hello", package = "greeter-plugin", settings = new Dictionary<string, string> { ["GREETING"] = "Hi" } }));
        Assert.Equal("Installed hello (greeter-plugin 1.0.0)", installed.GetProperty("note").GetString());
        var run = await Json(await client.PostAsJsonAsync("/console/v1/functions/run", new { name = "hello.greet", data = new { name = "Ann" } }));
        Assert.Equal("Hi Ann (1.0.0)", run.GetProperty("result").GetString());

        var list = (await Json(await client.GetAsync("/console/v1/functions/plugins"))).GetProperty("plugins")[0];
        Assert.True(list.GetProperty("curated").GetBoolean());
        Assert.Equal(("hello", "console", false, "HELLO__API_KEY"), (list.GetProperty("id").GetString(), list.GetProperty("source").GetString(),
            list.GetProperty("update").GetBoolean(), list.GetProperty("secretsNeeded")[0].GetString()));
        var page = await Json(await client.GetAsync("/console/v1/functions"));
        Assert.Equal("hello", page.GetProperty("current").GetProperty("functions")[0].GetProperty("plugin").GetString());
        Assert.Equal("greeter-plugin", page.GetProperty("current").GetProperty("plugins")[0].GetProperty("package").GetString());

        await Json(await client.PutAsJsonAsync("/console/v1/functions/plugins/hello", new { settings = new Dictionary<string, string> { ["GREETING"] = "Hello" } }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/console/v1/functions/secrets/HELLO__API_KEY", new { value = "k-123456" })).StatusCode);
        options.ProvidedPlugins["greeter-plugin"] = Plugin("1.1.0");
        Assert.True((await Json(await client.GetAsync("/console/v1/functions/plugins"))).GetProperty("plugins")[0].GetProperty("update").GetBoolean());
        await Json(await client.PostAsJsonAsync("/console/v1/functions/plugins/hello/upgrade", new { }));
        run = await Json(await client.PostAsJsonAsync("/console/v1/functions/run", new { name = "hello.greet", data = new { name = "Ann" } }));
        Assert.Equal("Hello Ann (1.1.0)", run.GetProperty("result").GetString());

        // A deploy that lists its plugins (winche.json) gets exactly those; unchanged ones keep where they came from.
        var deployed = await Json(await client.PostAsJsonAsync("/console/v1/functions/deploy", new
        {
            note = "Own code", files = new Dictionary<string, string> { ["index.ts"] = "import { callable } from \"@winche-technologies/winche-functions\"; export const own = callable(async () => 1);" },
            plugins = new[] { new { id = "hello", package = "greeter-plugin", version = "1.1.0", settings = new Dictionary<string, string> { ["GREETING"] = "Hello" } }, new { id = "hey", package = "greeter-plugin", version = (string?)null, settings = new Dictionary<string, string> { ["GREETING"] = "Hey" } } },
        }));
        Assert.Equal(["hello:console", "hey:deploy"], deployed.GetProperty("plugins").EnumerateArray().Select(p => $"{p.GetProperty("id").GetString()}:{p.GetProperty("source").GetString()}"));

        // The pages about a package and a copy: its README, versions, search, history, and where its functions answer.
        var about = await Json(await client.GetAsync("/console/v1/functions/plugins/package?name=greeter-plugin"));
        Assert.Equal("# Greeter", about.GetProperty("about").GetProperty("readme").GetString());
        Assert.True(about.GetProperty("curated").GetBoolean());
        Assert.Equal("1.1.0", (await Json(await client.GetAsync("/console/v1/functions/plugins/versions?name=greeter-plugin"))).GetProperty("versions")[0].GetProperty("version").GetString());
        var search = (await Json(await client.GetAsync("/console/v1/functions/plugins/search?q=greet"))).GetProperty("results");
        Assert.Equal(("greeter-plugin", true), (search[0].GetProperty("package").GetString(), search[0].GetProperty("provided").GetBoolean()));
        var history = (await Json(await client.GetAsync("/console/v1/functions/plugins/hello/history"))).GetProperty("history");
        Assert.Equal(["1.0.0 → 1.1.0", "Settings changed: GREETING", "Installed greeter-plugin 1.0.0"], history.EnumerateArray().Select(h => h.GetProperty("what").GetString()));
        Assert.EndsWith("/functions/v1/http", (await Json(await client.GetAsync("/console/v1/functions/plugins"))).GetProperty("functionsUrl").GetString());

        await Json(await client.DeleteAsync("/console/v1/functions/plugins/hello?secrets=remove"));
        var secrets = await (await client.GetAsync("/console/v1/functions/secrets")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("HELLO__API_KEY", secrets);
        Assert.Equal(["hey"], (await Json(await client.GetAsync("/console/v1/functions/plugins"))).GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }
}
