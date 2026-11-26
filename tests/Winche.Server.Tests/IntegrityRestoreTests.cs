using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Winche.Server.Tests;

/// <summary>Restoring or resetting Integrity's settings: an app that goes takes its web check's secret and its devices' keys with it.</summary>
[Collection("postgres")]
public class IntegrityRestoreTests(PostgresFixture pg)
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task AddWebAppAsync(HttpClient console, IServiceProvider project, string id)
    {
        await Json(await console.PostAsJsonAsync("/console/v1/projects/wards/integrity/apps", new { name = id, id, webSiteKey = "0x4AAAAAAAsitekey123" }));
        Assert.Equal(HttpStatusCode.NoContent, (await console.PutAsJsonAsync($"/console/v1/projects/wards/integrity/apps/{id}/turnstile-secret", new { value = "0x4AAAAAAAsecretsecret" })).StatusCode);
        await project.GetRequiredService<Winche.Integrity.IntegrityStore>().AddKeyAsync($"{id}-device", id, "appAttest", [1, 2, 3], development: false, receipt: null);
    }

    private static async Task<bool> LeftBehindAsync(IServiceProvider project, string id) =>
        await project.GetRequiredService<Winche.Secrets.SecretStore>().InfoAsync(Winche.Integrity.WincheIntegrity.TurnstileSecret(id)) is not null
        || await project.GetRequiredService<Winche.Integrity.IntegrityStore>().FindKeyAsync($"{id}-device") is not null;

    [Fact]
    public async Task Restoring_a_version_without_an_app_or_resetting_removes_what_the_app_left()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files, ("Winche:RequireTwoFactorForSettings", "false"));
        var console = server.CreateClient();
        await Json(await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" }));
        await Json(await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" }));
        await TestProjects.CreateAsync(console, "wards", "Wards", "authentication");
        var project = server.ProjectServices("wards");

        // A version from before any app.
        await Json(await console.PutAsJsonAsync("/console/v1/projects/wards/integrity/enforcement", new { enforcement = new Dictionary<string, string> { ["database"] = "off" }, reason = "Before apps" }));
        var before = (await Json(await console.GetAsync("/console/v1/projects/wards/auth/settings/history?area=integrity"))).GetProperty("versions")
            .EnumerateArray().First(v => v.GetProperty("reason").GetString() == "Before apps").GetProperty("id").GetInt64();
        await AddWebAppAsync(console, project, "web-app");
        Assert.True(await LeftBehindAsync(project, "web-app"));

        await Json(await console.PostAsJsonAsync($"/console/v1/projects/wards/auth/settings/history/{before}/restore", new { reason = "Undo" }));
        Assert.Null(project.GetRequiredService<Winche.Integrity.WincheIntegrity>().Find("web-app"));
        Assert.False(await LeftBehindAsync(project, "web-app"));

        // Resetting to the defaults: every app goes, and what each left.
        await AddWebAppAsync(console, project, "web-two");
        await Json(await console.PostAsJsonAsync("/console/v1/projects/wards/auth/settings/integrity/reset", new { reason = "Start over" }));
        Assert.False(await LeftBehindAsync(project, "web-two"));
        Directory.Delete(files, recursive: true);
    }

    [Fact]
    public async Task Apps_a_restore_takes_away_lose_their_keys_and_secrets_and_a_refused_secret_is_named_not_fatal()
    {
        var keysRemoved = new List<string>();
        var secretsDeleted = new List<string>();
        var left = await Winche.Console.Api.ConsoleAuthSettingsEndpoints.RemoveAppLeftoversAsync(["web-one", "web-two", "web-three"],
            id => { keysRemoved.Add(id); return Task.CompletedTask; },
            id => id == "web-two" ? throw new InvalidOperationException("AWS refused") : Task.Run(() => secretsDeleted.Add(id)),
            null, default);
        Assert.Equal(["web-one", "web-two", "web-three"], keysRemoved);   // every app's devices' keys
        Assert.Equal(["web-one", "web-three"], secretsDeleted);           // the others' secrets, past the refusal
        Assert.Equal(["web-two"], left);                                  // and the one left is named
    }
}
