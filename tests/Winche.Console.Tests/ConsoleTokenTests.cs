using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Winche.Console.Tests;

[Collection("postgres")]
public class ConsoleTokenTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fx.ResetAuthAsync();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            DO $$ BEGIN
              IF to_regclass('winche_console_tokens') IS NOT NULL THEN TRUNCATE winche_console_tokens; END IF;
              IF to_regclass('winche_console_cli_codes') IS NOT NULL THEN TRUNCATE winche_console_cli_codes; END IF;
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

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpClient Bearer(ConsoleAppFactory app, string token)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Winche_login_is_allowed_in_the_browser_and_the_token_acts_as_that_user_until_logout()
    {
        using var app = new ConsoleAppFactory(fx);
        using var browser = app.CreateClient();
        await browser.SetupAdminAsync();
        await browser.LoginAsync();

        // The CLI's PKCE pair; the signed-in user allows it, and the browser is sent back to the CLI with a code.
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var approved = await Json(await browser.PostAsJsonAsync("/_console/api/cli-login/approve",
            new { challenge, port = 9005, state = "s1", name = "winche CLI on laptop" }));
        var redirect = new Uri(approved.GetProperty("redirect").GetString()!);
        Assert.Equal(("127.0.0.1", 9005, "/callback"), (redirect.Host, redirect.Port, redirect.AbsolutePath));
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("s1", query["state"]);

        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/_console/api/cli-login/token", new { code = query["code"], verifier = "wrong" })).StatusCode);
        // A wrong verifier used the code up: the CLI starts again.
        approved = await Json(await browser.PostAsJsonAsync("/_console/api/cli-login/approve", new { challenge, port = 9005, state = "s2", name = "winche CLI on laptop" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var login = await Json(await anon.PostAsJsonAsync("/_console/api/cli-login/token", new { code, verifier }));
        Assert.Equal("admin@example.com", login.GetProperty("user").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/_console/api/cli-login/token", new { code, verifier })).StatusCode);   // once

        // A session of the account: its access token is the user, with their role here.
        using var cli = Bearer(app, login.GetProperty("accessToken").GetString()!);
        await Json(await cli.GetAsync("/_console/api/rules/database/versions"));
        // It can't approve another CLI (only a person in the browser can), but it makes CI tokens (winche tokens:create);
        // a CI token can't.
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.PostAsJsonAsync("/_console/api/cli-login/approve", new { challenge, port = 9005, state = "x" })).StatusCode);
        var made = await Json(await cli.PostAsJsonAsync("/_console/api/tokens", new { name = "x", scopes = new[] { "rules" }, days = 1 }));
        using var ci = Bearer(app, made.GetProperty("secret").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.PostAsJsonAsync("/_console/api/tokens", new { name = "y", scopes = new[] { "rules" }, days = 1 })).StatusCode);   // not one of its scopes

        // It's renewed with its refresh token (which turns over), like any sign-in.
        var refresh = login.GetProperty("refreshToken").GetString()!;
        var renewed = await Json(await anon.PostAsJsonAsync("/_console/api/cli-login/refresh", new { refreshToken = refresh }));
        using var cli2 = Bearer(app, renewed.GetProperty("accessToken").GetString()!);
        await Json(await cli2.GetAsync("/_console/api/rules/database/versions"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/_console/api/cli-login/refresh", new { refreshToken = refresh })).StatusCode);   // used

        // Signed out everywhere (from the browser), the CLI's access token stops working within seconds, not when it runs out.
        var me = JsonDocument.Parse(await browser.GetStringAsync("/_console/api/auth/state")).RootElement.GetProperty("user").GetProperty("id").GetString();
        var kept = await Json(await anon.PostAsJsonAsync("/_console/api/cli-login/refresh", new { refreshToken = renewed.GetProperty("refreshToken").GetString() }));
        using var cli3 = Bearer(app, kept.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await cli3.GetAsync("/_console/api/rules/database/versions")).StatusCode);
        var accounts = app.Services.GetRequiredService<Winche.Console.Identity.ConsoleAccounts>();
        Assert.True(await accounts.UseAsync(a => a.RevokeSessionsAsync(me!)) > 0);
        await Task.Delay(TimeSpan.FromSeconds(11));   // sessions are looked up again after 10 s
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli3.GetAsync("/_console/api/rules/database/versions")).StatusCode);
        renewed = kept;
        await browser.LoginAsync();

        // winche logout ends the session: it can't be renewed.
        var current = renewed.GetProperty("refreshToken").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/_console/api/cli-login/logout", new { refreshToken = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/_console/api/cli-login/refresh", new { refreshToken = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(app, "a.b.c").GetAsync("/_console/api/rules/database/versions")).StatusCode);
    }

    [Fact]
    public async Task A_cli_sign_in_stops_when_its_account_is_turned_off()
    {
        using var app = new ConsoleAppFactory(fx);
        using var browser = app.CreateClient();
        await browser.SetupAdminAsync();
        await browser.LoginAsync();
        await browser.PostAsJsonAsync("/_console/api/users", new { email = "m@example.com", role = "Member", password = "Passw0rd!" });
        using var member = app.CreateClient();
        await member.LoginAsync("m@example.com", "Passw0rd!");
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var approved = await Json(await member.PostAsJsonAsync("/_console/api/cli-login/approve", new { challenge, port = 9005, state = "s", name = "laptop" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var login = await Json(await app.CreateClient().PostAsJsonAsync("/_console/api/cli-login/token", new { code, verifier }));
        using var cli = Bearer(app, login.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/_console/api/project")).StatusCode);

        var users = JsonDocument.Parse(await browser.GetStringAsync("/_console/api/users")).RootElement;
        var id = users.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "m@example.com").GetProperty("id").GetString();
        await browser.PutAsJsonAsync($"/_console/api/users/{id}", new { active = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/_console/api/project")).StatusCode);
    }

    [Fact]
    public async Task A_ci_token_only_does_what_its_scopes_allow_and_stops_when_revoked()
    {
        using var app = new ConsoleAppFactory(fx);
        using var admin = app.CreateClient();
        await admin.SetupAdminAsync();
        await admin.LoginAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/_console/api/tokens", new { name = "x", scopes = new[] { "everything" }, days = 30 })).StatusCode);
        var made = await Json(await admin.PostAsJsonAsync("/_console/api/tokens", new { name = "gitlab-main", scopes = new[] { "rules" }, days = 30 }));
        var secret = made.GetProperty("secret").GetString()!;
        Assert.StartsWith("wct_", secret);
        using var ci = Bearer(app, secret);

        await Json(await ci.GetAsync("/_console/api/rules/database/versions"));                                  // rules: yes
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/_console/api/tokens")).StatusCode);       // anything else: no
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/_console/api/app-auth/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(app, secret[..^1] + "x").GetAsync("/_console/api/rules/database/versions")).StatusCode);

        var listed = (await Json(await admin.GetAsync("/_console/api/tokens"))).GetProperty("tokens").EnumerateArray().Single();
        Assert.Equal(("ci", "gitlab-main", "rules"), (listed.GetProperty("kind").GetString(), listed.GetProperty("name").GetString(), listed.GetProperty("scopes")[0].GetString()));
        Assert.Equal(JsonValueKind.String, listed.GetProperty("lastUsedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/_console/api/tokens/{listed.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/_console/api/rules/database/versions")).StatusCode);
    }
}
