using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Winche.Console.Tests;

[Collection("postgres")]
public class SessionRenewalTests(PostgresFixture fx)
{
    [Fact]
    public async Task Requests_that_renew_the_session_at_once_all_stay_signed_in()
    {
        await fx.ResetAuthAsync();
        using var app = new ConsoleAppFactory(fx);
        var setup = app.CreateClient();
        await setup.SetupAdminAsync();
        // Due for renewal from the start: every request renews.
        Winche.Console.Identity.ConsoleSession.CheckEvery = TimeSpan.Zero;
        var login = await setup.PostAsJsonAsync("/_console/api/auth/login", new { email = "admin@example.com", password = "Passw0rd!" });
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        // A page's requests, all at once, with the same cookie.
        try
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                var c = app.CreateClient(new() { HandleCookies = false });
                c.DefaultRequestHeaders.Add("Cookie", cookie);
                var resp = await c.GetAsync("/_console/api/auth/state");
                var state = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                var renewed = resp.Headers.TryGetValues("Set-Cookie", out var set) ? string.Join("; ", set.Select(x => x.Split(';')[0])) : null;
                return (signedIn: state.GetProperty("user").ValueKind == JsonValueKind.Object, renewed);
            }));
            Assert.All(answers, a => Assert.True(a.signedIn));

            // Whichever renewed cookie the browser keeps, it's still signed in.
            foreach (var renewed in answers.Select(a => a.renewed).Where(r => r is not null).Distinct())
            {
                var c = app.CreateClient(new() { HandleCookies = false });
                c.DefaultRequestHeaders.Add("Cookie", renewed!);
                var state = JsonDocument.Parse(await (await c.GetAsync("/_console/api/auth/state")).Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(JsonValueKind.Object, state.GetProperty("user").ValueKind);
            }
        }
        finally { Winche.Console.Identity.ConsoleSession.CheckEvery = TimeSpan.FromMinutes(5); }
    }

    [Fact]
    public async Task An_account_turned_off_is_signed_out_at_its_next_request()
    {
        await fx.ResetAuthAsync();
        using var app = new ConsoleAppFactory(fx);
        var admin = app.CreateClient();
        await admin.SetupAdminAsync();
        await admin.LoginAsync();
        await admin.PostAsJsonAsync("/_console/api/users", new { email = "m@example.com", role = "Member", password = "Passw0rd!" });
        var member = app.CreateClient();
        await member.LoginAsync("m@example.com", "Passw0rd!");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await member.GetAsync("/_console/api/project")).StatusCode);

        var users = JsonDocument.Parse(await admin.GetStringAsync("/_console/api/users")).RootElement;
        var id = users.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "m@example.com").GetProperty("id").GetString();
        await admin.PutAsJsonAsync($"/_console/api/users/{id}", new { active = false });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await member.GetAsync("/_console/api/project")).StatusCode);   // not minutes later
    }
}
