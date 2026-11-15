using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Console.Users;
using Xunit;

namespace Winche.Console.Tests;

/// <summary>Authentication settings: sensitive ones need two-step sign-in; there's no user directory setting (Winche Auth's users).</summary>
[Collection("postgres")]
public class ConsoleUserDirectoryTests(PostgresFixture fx) : IAsyncLifetime
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
            END $$;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Sensitive_settings_need_two_step_sign_in()
    {
        using var app = new ConsoleAppFactory(fx, ("Sample:RequireTwoFactor", "true"));
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        var refused = await client.PutAsJsonAsync("/_console/api/app-auth/settings/providers", new { settings = new { trustWincheAuth = true, providers = Array.Empty<object>() }, reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("TWO_FACTOR_REQUIRED", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_console/api/app-auth/settings/directory")).StatusCode);   // no user directory setting
        await Json(await client.GetAsync("/_console/api/app-auth/settings/providers"));   // reading is fine
    }
}
