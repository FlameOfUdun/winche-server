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
        var approved = await Json(await browser.PostAsJsonAsync("/console/v1/cli/approve",
            new { challenge, port = 9005, state = "s1", name = "winche CLI on laptop" }));
        var redirect = new Uri(approved.GetProperty("redirect").GetString()!);
        Assert.Equal(("127.0.0.1", 9005, "/callback"), (redirect.Host, redirect.Port, redirect.AbsolutePath));
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("s1", query["state"]);

        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/console/v1/cli/token", new { code = query["code"], verifier = "wrong" })).StatusCode);
        // A wrong verifier used the code up: the CLI starts again.
        approved = await Json(await browser.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 9005, state = "s2", name = "winche CLI on laptop" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var login = await Json(await anon.PostAsJsonAsync("/console/v1/cli/token", new { code, verifier }));
        Assert.Equal("admin@example.com", login.GetProperty("user").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/console/v1/cli/token", new { code, verifier })).StatusCode);   // once

        // A session of the account: its access token is the user, with their role here.
        using var cli = Bearer(app, login.GetProperty("accessToken").GetString()!);
        await Json(await cli.GetAsync("/console/v1/rules/database/versions"));
        // It can't approve another CLI (only a person in the browser can), but it makes service accounts (winche
        // accounts:create); a service account can't.
        Assert.Equal(HttpStatusCode.Forbidden, (await cli.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 9005, state = "x" })).StatusCode);
        var account = (await Json(await cli.PostAsJsonAsync("/console/v1/service-accounts", new { name = "cli-made-" + Guid.NewGuid().ToString("N")[..8], scopes = new[] { "rules" } }))).GetProperty("id").GetString();
        var key = (await Json(await cli.PostAsJsonAsync($"/console/v1/service-accounts/{account}/keys", new { days = 1 }))).GetProperty("secret").GetString();
        var access = (await Json(await anon.PostAsJsonAsync("/console/v1/service-accounts/token", new { key }))).GetProperty("accessToken").GetString()!;
        using var ci = Bearer(app, access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.PostAsJsonAsync("/console/v1/service-accounts", new { name = "y", scopes = new[] { "rules" } })).StatusCode);   // not one of its scopes

        // It's renewed with its refresh token (which turns over), like any sign-in.
        var refresh = login.GetProperty("refreshToken").GetString()!;
        var renewed = await Json(await anon.PostAsJsonAsync("/console/v1/cli/refresh", new { refreshToken = refresh }));
        using var cli2 = Bearer(app, renewed.GetProperty("accessToken").GetString()!);
        await Json(await cli2.GetAsync("/console/v1/rules/database/versions"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/console/v1/cli/refresh", new { refreshToken = refresh })).StatusCode);   // used

        // Signed out everywhere (from the browser), the CLI's access token stops working within seconds, not when it runs out.
        var me = JsonDocument.Parse(await browser.GetStringAsync("/console/v1/session/state")).RootElement.GetProperty("user").GetProperty("id").GetString();
        var kept = await Json(await anon.PostAsJsonAsync("/console/v1/cli/refresh", new { refreshToken = renewed.GetProperty("refreshToken").GetString() }));
        using var cli3 = Bearer(app, kept.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await cli3.GetAsync("/console/v1/rules/database/versions")).StatusCode);
        var accounts = app.Services.GetRequiredService<Winche.Console.Identity.ConsoleAccounts>();
        Assert.True(await accounts.UseAsync(a => a.RevokeSessionsAsync(me!)) > 0);
        await Task.Delay(TimeSpan.FromSeconds(11));   // sessions are looked up again after 10 s
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli3.GetAsync("/console/v1/rules/database/versions")).StatusCode);
        renewed = kept;
        await browser.LoginAsync();

        // winche logout ends the session: it can't be renewed.
        var current = renewed.GetProperty("refreshToken").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/console/v1/cli/logout", new { refreshToken = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/console/v1/cli/refresh", new { refreshToken = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(app, "a.b.c").GetAsync("/console/v1/rules/database/versions")).StatusCode);
    }

    [Fact]
    public async Task A_cli_sign_in_stops_when_its_account_is_turned_off()
    {
        using var app = new ConsoleAppFactory(fx);
        using var browser = app.CreateClient();
        await browser.SetupAdminAsync();
        await browser.LoginAsync();
        await browser.PostAsJsonAsync("/console/v1/accounts", new { email = "m@example.com", role = "Member", password = "Passw0rd!" });
        using var member = app.CreateClient();
        await member.LoginAsync("m@example.com", "Passw0rd!");
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var approved = await Json(await member.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 9005, state = "s", name = "laptop" }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(approved.GetProperty("redirect").GetString()!).Query)["code"];
        var login = await Json(await app.CreateClient().PostAsJsonAsync("/console/v1/cli/token", new { code, verifier }));
        using var cli = Bearer(app, login.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/console/v1/project")).StatusCode);

        var users = JsonDocument.Parse(await browser.GetStringAsync("/console/v1/accounts")).RootElement;
        var id = users.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "m@example.com").GetProperty("id").GetString();
        await browser.PutAsJsonAsync($"/console/v1/accounts/{id}", new { active = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/console/v1/project")).StatusCode);
    }

    [Fact]
    public async Task A_ci_token_becomes_a_service_accounts_key_and_works_only_through_the_exchange()
    {
        using var app = new ConsoleAppFactory(fx);
        using var admin = app.CreateClient();
        await admin.SetupAdminAsync();
        await admin.LoginAsync();

        // A CI token as an earlier version kept it (wct_<id>_<secret>, the secret's hash).
        var store = app.Services.GetRequiredService<Winche.Console.Identity.ConsoleTokenStore>();
        await store.CreateCodeAsync(new string('a', 43), "u1", "admin@example.com", "Admin", "make the tables");
        var db = app.Services.GetRequiredKeyedService<NpgsqlDataSource>("WincheConfigOptions");
        var (id, secret) = (Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)));
        await using (var cmd = db.CreateCommand("""
            INSERT INTO winche_console_tokens (id, kind, name, user_id, user_email, role, scopes, hash, created_at, expires_at)
            VALUES ($1, 'ci', 'gitlab-' || $1, 'u1', 'admin@example.com', 'Admin', ARRAY['rules'], $2, now(), now() + interval '30 days')
            """))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            await cmd.ExecuteNonQueryAsync();
        }
        var token = $"wct_{id}_{secret}";

        // It's no longer accepted as it is, but exchanged it's the account gitlab-<id>, deploying rules only.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(app, token).GetAsync("/console/v1/rules/database/versions")).StatusCode);
        using var anon = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var exchanged = await Json(await anon.PostAsJsonAsync("/console/v1/service-accounts/token", new { key = token }));
        Assert.Equal($"gitlab-{id}", exchanged.GetProperty("account").GetProperty("name").GetString());
        using var ci = Bearer(app, exchanged.GetProperty("accessToken").GetString()!);
        await Json(await ci.GetAsync("/console/v1/rules/database/versions"));                                          // rules: yes
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/console/v1/auth/config")).StatusCode);          // anything else: no
        var listed = (await Json(await admin.GetAsync("/console/v1/service-accounts"))).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == $"gitlab-{id}");
        Assert.Equal(($"gitlab-{id}", "rules", "admin@example.com"), (listed.GetProperty("name").GetString(), listed.GetProperty("scopes")[0].GetString(), listed.GetProperty("createdBy").GetString()));
        Assert.Equal(id, listed.GetProperty("keys")[0].GetProperty("id").GetString());

        // Revoking the key ends it.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/console/v1/service-accounts/{listed.GetProperty("id").GetString()}/keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/console/v1/service-accounts/token", new { key = token })).StatusCode);
    }
}
