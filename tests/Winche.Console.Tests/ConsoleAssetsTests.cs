using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Winche.Console.Tests;

/// <summary>The Assets page on the sample app: contents sent once, a release published to a channel, apps reading it.</summary>
[Collection("postgres")]
public class ConsoleAssetsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAuthAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_release_is_published_from_the_console_and_apps_read_its_channel_through_the_rules()
    {
        using var app = new ConsoleAppFactory(fx);
        using var client = app.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();
        Assert.True((await Json(await client.GetAsync("/_console/api/auth/state"))).GetProperty("capabilities").GetProperty("assets").GetBoolean());

        var stamp = Guid.NewGuid().ToString("N");   // contents unique to this run: the database is shared by the tests
        var hello = Encoding.UTF8.GetBytes($"hello {stamp}");
        var secret = Encoding.UTF8.GetBytes($"members only {stamp}");
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // Only contents the server doesn't have are sent.
        var missing = await Json(await client.PostAsJsonAsync("/_console/api/assets/contents/missing", new { hashes = new[] { Hash(hello), Hash(secret) } }));
        Assert.Equal(2, missing.GetProperty("missing").GetArrayLength());
        foreach (var b in new[] { hello, secret })
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/_console/api/assets/contents/{Hash(b)}", new ByteArrayContent(b))).StatusCode);
        Assert.Equal(0, (await Json(await client.PostAsJsonAsync("/_console/api/assets/contents/missing", new { hashes = new[] { Hash(hello) } }))).GetProperty("missing").GetArrayLength());

        var release = await Json(await client.PostAsJsonAsync("/_console/api/assets/releases", new
        {
            note = "First words", channel = $"run-{stamp[..8]}",
            files = new Dictionary<string, object>
            {
                ["public/hello.txt"] = new { hash = Hash(hello), size = hello.Length, mimeType = "text/plain" },
                ["members/notes.txt"] = new { hash = Hash(secret), size = secret.Length, mimeType = "text/plain" },
            },
        }));
        var channel = release.GetProperty("channel").GetString()!;
        Assert.Equal(2, release.GetProperty("files").GetInt32());

        // Apps: the channel lists the release, and files are read through the assets rules.
        using var anon = app.CreateClient();
        var listing = await Json(await anon.GetAsync($"/assets/v1/channels/{channel}"));
        Assert.Equal(release.GetProperty("number").GetInt32(), listing.GetProperty("release").GetInt32());
        Assert.Equal($"hello {stamp}", await anon.GetStringAsync($"/assets/v1/channels/{channel}/files/public/hello.txt"));
        var refused = await anon.GetAsync($"/assets/v1/channels/{channel}/files/members/notes.txt");
        Assert.False(refused.IsSuccessStatusCode);

        // The console's history of that channel.
        var history = await Json(await client.GetAsync($"/_console/api/assets/channels/{channel}/history"));
        Assert.Single(history.GetProperty("moves").EnumerateArray());
    }
}
