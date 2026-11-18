using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Winche.Server.Tests;

/// <summary>Service accounts: keys and trusted identities exchanged for an hour's access token, limited by the account's role.</summary>
[Collection("postgres")]
public class ServiceAccountTests(PostgresFixture pg)
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> AdminAsync(ServerFactory server)
    {
        var admin = server.CreateClient();
        await Json(await admin.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        return admin;
    }

    private static HttpClient As(ServerFactory server, string accessToken)
    {
        var client = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static object Note(string by) => new { op = "db.add", args = new { collection = "notes", fields = new { by = new { stringValue = by } } } };

    [Fact]
    public async Task Keys_are_exchanged_for_access_tokens_that_act_with_the_accounts_role_in_its_project_only()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "database", "functions");
        await TestProjects.CreateAsync(admin, "wards", "Wards", "database");
        const string sa = "/console/v1/projects/clinic/service-accounts";

        // An account with a role, and a key (shown once).
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(sa, new { name = "billing", role = "member", scopes = new[] { "functions" } })).StatusCode);   // one or the other
        var billing = (await Json(await admin.PostAsJsonAsync(sa, new { name = "billing", role = "member" }))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(sa, new { name = "billing", role = "viewer" })).StatusCode);   // names are unique
        var key = (await Json(await admin.PostAsJsonAsync($"{sa}/{billing}/keys", new { days = 30 }))).GetProperty("secret").GetString()!;
        Assert.StartsWith("wsk_", key);

        // The key is never sent with calls: it's exchanged for an access token.
        using var anonymous = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{sa}/token", new { key = key + "x" })).StatusCode);
        var exchanged = await Json(await anonymous.PostAsJsonAsync($"{sa}/token", new { key }));
        var token = exchanged.GetProperty("accessToken").GetString()!;
        Assert.StartsWith("wsa_", token);
        Assert.True(exchanged.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow <= TimeSpan.FromHours(1));
        using var asBilling = As(server, token);
        using (var keyAsBearer = As(server, key))
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyAsBearer.PostAsJsonAsync("/console/v1/projects/clinic/admin", Note("key"))).StatusCode);

        // A member: writes the data (named in the history), but isn't an admin, and has nothing in another project.
        var path = (await Json(await asBilling.PostAsJsonAsync("/console/v1/projects/clinic/admin", Note("billing")))).GetProperty("result").GetProperty("document").GetProperty("path").GetString();
        var history = await Json(await admin.GetAsync($"/console/v1/projects/clinic/documents/history?path={path}"));
        Assert.Equal("console:billing (service account)", history.GetProperty("entries")[0].GetProperty("actor").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await asBilling.GetAsync("/console/v1/projects/clinic/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asBilling.GetAsync(sa)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asBilling.PostAsJsonAsync("/console/v1/projects/wards/admin", Note("elsewhere"))).StatusCode);

        // At most two keys (one replaces the other with no downtime); a revoked key can't be exchanged.
        var second = await Json(await admin.PostAsJsonAsync($"{sa}/{billing}/keys", new { days = 30 }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{sa}/{billing}/keys", new { days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{sa}/{billing}/keys", new { days = 1000 })).StatusCode);
        var listed = await Json(await admin.GetAsync(sa));
        var account = listed.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "billing");
        Assert.Equal(2, account.GetProperty("keys").GetArrayLength());
        Assert.True(account.GetProperty("makerIsAdmin").GetBoolean());
        var firstKeyId = account.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetString() != second.GetProperty("key").GetProperty("id").GetString()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{sa}/{billing}/keys/{firstKeyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{sa}/token", new { key })).StatusCode);
        Assert.StartsWith("wsa_", (await Json(await anonymous.PostAsJsonAsync($"{sa}/token", new { key = second.GetProperty("secret").GetString() }))).GetProperty("accessToken").GetString());

        // Turned off: no exchange.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"{sa}/{billing}", new { role = "member", disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{sa}/token", new { key = second.GetProperty("secret").GetString() })).StatusCode);

        // A deploy-only account: deploying, nothing else.
        var deployer = (await Json(await admin.PostAsJsonAsync(sa, new { name = "ci-deploy", scopes = new[] { "functions" } }))).GetProperty("id").GetString()!;
        var deployKey = (await Json(await admin.PostAsJsonAsync($"{sa}/{deployer}/keys", new { days = 7 }))).GetProperty("secret").GetString();
        using var asDeployer = As(server, (await Json(await anonymous.PostAsJsonAsync($"{sa}/token", new { key = deployKey }))).GetProperty("accessToken").GetString()!);
        var deployed = await asDeployer.PostAsJsonAsync("/console/v1/projects/clinic/functions/deploy", new { note = "From CI", files = new Dictionary<string, string>
        {
            ["index.ts"] = """import { callable } from "@winche-technologies/winche-functions"; export const hi = callable(() => "hi");""",
        } });
        Assert.True(deployed.IsSuccessStatusCode, await deployed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await asDeployer.PostAsJsonAsync("/console/v1/projects/clinic/admin", Note("ci"))).StatusCode);

        // Service accounts can't manage service accounts, whatever their role.
        var root = (await Json(await admin.PostAsJsonAsync(sa, new { name = "automation", role = "admin" }))).GetProperty("id").GetString()!;
        var rootKey = (await Json(await admin.PostAsJsonAsync($"{sa}/{root}/keys", new { days = 7 }))).GetProperty("secret").GetString();
        using var asRoot = As(server, (await Json(await anonymous.PostAsJsonAsync($"{sa}/token", new { key = rootKey }))).GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await asRoot.GetAsync("/console/v1/projects/clinic/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asRoot.PostAsJsonAsync(sa, new { name = "more", role = "admin" })).StatusCode);
    }

    [Fact]
    public async Task A_trusted_identity_signs_an_account_in_with_no_key()
    {
        await using var ci = new FakeOidcProvider();
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var admin = await AdminAsync(server);
        await TestProjects.CreateAsync(admin, "clinic", "Clinic", "database");
        const string sa = "/console/v1/projects/clinic/service-accounts";
        var deploy = (await Json(await admin.PostAsJsonAsync(sa, new { name = "ci-deploy", role = "member" }))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{sa}/{deploy}/identities", new { issuer = ci.Issuer, subject = "*", audience = "winche" })).StatusCode);   // not anyone
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{sa}/{deploy}/identities", new { issuer = "http://ci.example.com", subject = "x", audience = "winche" })).StatusCode);   // https
        await Json(await admin.PostAsJsonAsync($"{sa}/{deploy}/identities", new { issuer = ci.Issuer, subject = "project_path:team/app:ref:main:*", audience = "https://winche.example.com/projects/clinic" }));

        using var anonymous = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        async Task<HttpResponseMessage> Exchange(string sub, string aud) =>
            await anonymous.PostAsJsonAsync($"{sa}/token", new { assertion = ci.WorkloadToken(sub, aud) });
        var ok = await Json(await Exchange("project_path:team/app:ref:main:job:42", "https://winche.example.com/projects/clinic"));
        Assert.Equal(("ci-deploy", "identity"), (ok.GetProperty("account").GetProperty("name").GetString(), ok.GetProperty("account").GetProperty("via").GetString()));
        using (var asCi = As(server, ok.GetProperty("accessToken").GetString()!))
            await Json(await asCi.PostAsJsonAsync("/console/v1/projects/clinic/admin", Note("ci")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Exchange("project_path:team/app:ref:feature:job:43", "https://winche.example.com/projects/clinic")).StatusCode);   // another branch
        Assert.Equal(HttpStatusCode.Unauthorized, (await Exchange("project_path:team/app:ref:main:job:44", "https://other.example.com")).StatusCode);   // made for someone else
        await using (var stranger = new FakeOidcProvider())   // the same subject from an issuer nobody trusts
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{sa}/token",
                new { assertion = stranger.WorkloadToken("project_path:team/app:ref:main:job:45", "https://winche.example.com/projects/clinic") })).StatusCode);
        var listed = await Json(await admin.GetAsync(sa));
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("accounts")[0].GetProperty("identities")[0].GetProperty("lastUsedAt").ValueKind);
    }
}
