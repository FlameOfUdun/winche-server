using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Winche.Server.Tests;

/// <summary>Sign-in through an identity provider: where it may send you back, and adding a provider only as yourself, on purpose.</summary>
[Collection("postgres")]
public class ConsoleSignInHardeningTests(PostgresFixture pg)
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> FollowAsync(HttpClient console, FakeOidcProvider idp, HttpResponseMessage response)
    {
        using var network = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        for (var i = 0; i < 5 && response.StatusCode == HttpStatusCode.Redirect; i++)
        {
            var next = response.Headers.Location!;
            if (next.IsAbsoluteUri && next.OriginalString.StartsWith(idp.Issuer)) response = await network.GetAsync(next);
            else if (next.IsAbsoluteUri && next.Host == "localhost") response = await console.GetAsync(next.PathAndQuery);
            else return response;
        }
        return response;
    }

    private static Task<HttpResponseMessage> FollowAsync(HttpClient console, FakeOidcProvider idp, string path) =>
        console.GetAsync(path).ContinueWith(t => FollowAsync(console, idp, t.Result)).Unwrap();

    private async Task<(ServerFactory Server, HttpClient Admin)> StartAsync(FakeOidcProvider idp)
    {
        var server = new ServerFactory(await pg.NewDatabaseAsync(), Directory.CreateTempSubdirectory("winche-server-files-").FullName, ("Winche:RequireTwoFactorForSettings", "false"));
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
        return (server, admin);
    }

    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r/evil.example")]
    [InlineData("/ /evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/")]
    public async Task Coming_back_goes_only_to_a_page_of_this_console(string returnUrl)
    {
        await using var idp = new FakeOidcProvider();
        var (server, _) = await StartAsync(idp);
        await using var _s = server;
        idp.Next = new() { ["sub"] = "new-1", ["email"] = "dora@example.com", ["email_verified"] = true };
        var client = server.CreateClient(new() { AllowAutoRedirect = false });
        var landed = await FollowAsync(client, idp, "/console/v1/session/oidc/start?provider=staff&returnUrl=" + Uri.EscapeDataString(returnUrl));
        var location = landed.Headers.Location!.OriginalString;
        Assert.DoesNotContain("evil", location);
        Assert.StartsWith("/console/", location);
    }

    [Fact]
    public async Task Adding_a_provider_starts_only_from_this_console_and_asks_the_provider_to_sign_in_again()
    {
        await using var idp = new FakeOidcProvider();
        var (server, admin) = await StartAsync(idp);
        await using var _s = server;
        var fromElsewhere = new HttpRequestMessage(HttpMethod.Get, "/console/v1/session/oidc/start?provider=staff&link=true&returnUrl=/console/");
        fromElsewhere.Headers.Add("Sec-Fetch-Site", "cross-site");
        var refused = await admin.SendAsync(fromElsewhere);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/console/", refused.Headers.Location!.OriginalString);
        Assert.Contains("signInError=", refused.Headers.Location!.OriginalString);

        var fromHere = new HttpRequestMessage(HttpMethod.Get, "/console/v1/session/oidc/start?provider=staff&link=true&returnUrl=/console/");
        fromHere.Headers.Add("Sec-Fetch-Site", "same-origin");
        var toProvider = await admin.SendAsync(fromHere);
        Assert.StartsWith(idp.Issuer, toProvider.Headers.Location!.OriginalString);
        Assert.Contains("prompt=login", toProvider.Headers.Location!.Query);

        // Not signed in: back to the console with why, not a bare error.
        var nobody = await server.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/console/v1/session/oidc/start?provider=staff&link=true&returnUrl=/console/");
        Assert.Equal(HttpStatusCode.Redirect, nobody.StatusCode);
        Assert.Contains("signInError=", nobody.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_provider_is_added_only_to_the_account_still_signed_in_when_it_comes_back()
    {
        await using var idp = new FakeOidcProvider();
        var (server, admin) = await StartAsync(idp);
        await using var _s = server;
        await Json(await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "bob@example.com", role = "none", password = "Passw0rd!long" }));
        var start = await admin.GetAsync("/console/v1/session/oidc/start?provider=staff&link=true&returnUrl=/console/");   // the admin starts it…
        await admin.PostAsync("/console/v1/session/logout", null);                                                      // …and Bob signs in meanwhile
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "bob@example.com", password = "Passw0rd!long" }));
        idp.Next = new() { ["sub"] = "someone", ["email"] = "admin@example.com", ["email_verified"] = true };
        var landed = await FollowAsync(admin, idp, start);
        Assert.Contains("signInError=", landed.Headers.Location!.OriginalString);
        Assert.False((await Json(await admin.GetAsync("/console/v1/session/sign-in-methods"))).GetProperty("providers").EnumerateArray().Single().GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task An_account_with_a_password_is_never_claimed_by_its_address_even_before_its_first_sign_in()
    {
        await using var idp = new FakeOidcProvider();
        var (server, admin) = await StartAsync(idp);
        await using var _s = server;
        await Json(await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "carol@example.com", role = "none", password = "Passw0rd!long" }));
        idp.Next = new() { ["sub"] = "carol-1", ["email"] = "carol@example.com", ["email_verified"] = true };
        var client = server.CreateClient(new() { AllowAutoRedirect = false });
        var landed = await FollowAsync(client, idp, "/console/v1/session/oidc/start?provider=staff");
        Assert.Contains("signInError=", landed.Headers.Location!.OriginalString);
        Assert.Equal(JsonValueKind.Null, (await Json(await client.GetAsync("/console/v1/session/state"))).GetProperty("user").ValueKind);
    }
}
