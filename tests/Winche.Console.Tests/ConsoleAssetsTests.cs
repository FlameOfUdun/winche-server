using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.True((await Json(await client.GetAsync("/console/v1/session/state"))).GetProperty("capabilities").GetProperty("assets").GetBoolean());

        var stamp = Guid.NewGuid().ToString("N");   // contents unique to this run: the database is shared by the tests
        var hello = Encoding.UTF8.GetBytes($"hello {stamp}");
        var secret = Encoding.UTF8.GetBytes($"members only {stamp}");
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // Only contents the server doesn't have are sent.
        var missing = await Json(await client.PostAsJsonAsync("/console/v1/assets/contents/missing", new { hashes = new[] { Hash(hello), Hash(secret) } }));
        Assert.Equal(2, missing.GetProperty("missing").GetArrayLength());
        foreach (var b in new[] { hello, secret })
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/console/v1/assets/contents/{Hash(b)}", new ByteArrayContent(b))).StatusCode);
        Assert.Equal(0, (await Json(await client.PostAsJsonAsync("/console/v1/assets/contents/missing", new { hashes = new[] { Hash(hello) } }))).GetProperty("missing").GetArrayLength());

        var release = await Json(await client.PostAsJsonAsync("/console/v1/assets/releases", new
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
        var history = await Json(await client.GetAsync($"/console/v1/assets/channels/{channel}/history"));
        Assert.Single(history.GetProperty("moves").EnumerateArray());
    }
}

/// <summary>Assets on a CDN through the console: the CDN set up, a file read for its hash, published, apps given signed links.</summary>
[Collection("postgres")]
public class ConsoleAssetsCdnTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAuthAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A CloudFront stand-in: one file, served only to links signed with its key pair (canned policy, not expired).</summary>
    private sealed class FakeCloudFront(RSA key, string keyPairId, string url, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var at = uri.GetLeftPart(UriPartial.Path);
            var ok = q["Key-Pair-Id"] == keyPairId && long.TryParse(q["Expires"], out var epoch) && epoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && q["Signature"] is { } sig && key.VerifyData(Encoding.UTF8.GetBytes(
                    "{\"Statement\":[{\"Resource\":\"" + at + "\",\"Condition\":{\"DateLessThan\":{\"AWS:EpochTime\":" + epoch + "}}}]}"),
                    Convert.FromBase64String(sig.Replace('-', '+').Replace('_', '=').Replace('~', '/')), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            if (!ok) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (at != url) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes) };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(r);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_cdn_is_set_up_its_files_published_by_address_and_apps_only_get_signed_links()
    {
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var id = $"cdn-{stamp}";
        var url = $"https://media-{stamp}.example.com/app/bvm.mp4";
        var bytes = Encoding.UTF8.GetBytes($"video {stamp}");
        using var key = RSA.Create(2048);
        using var app = new ConsoleAppFactory(fx);
        using var cdnApp = app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton(new Winche.Assets.AssetCdnHttp(new HttpClient(new FakeCloudFront(key, "K2KEY", url, bytes))))));
        using var client = cdnApp.CreateClient();
        await client.SetupAdminAsync();
        await client.LoginAsync();

        // Its private key is a project secret, never shown again.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/console/v1/assets/cdns/{id}", new
            { baseUrl = $"https://media-{stamp}.example.com/app/", keyPairId = "K2KEY", reason = "no key" })).StatusCode);
        await Json(await client.PutAsJsonAsync($"/console/v1/assets/cdns/{id}", new
            { baseUrl = $"https://media-{stamp}.example.com/app", keyPairId = "K2KEY", privateKey = key.ExportPkcs8PrivateKeyPem(), reason = "Videos" }));
        var cdns = await Json(await client.GetAsync("/console/v1/assets/cdns"));
        var mine = cdns.GetProperty("cdns").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        Assert.Equal($"https://media-{stamp}.example.com/app/", mine.GetProperty("baseUrl").GetString());
        Assert.DoesNotContain("PRIVATE KEY", cdns.ToString());
        Assert.True((await Json(await client.PostAsJsonAsync($"/console/v1/assets/cdns/{id}/test", new { url }))).GetProperty("ok").GetBoolean());

        // Its file: hashed by the server, published by address.
        var file = (await Json(await client.PostAsJsonAsync("/console/v1/assets/cdn/files", new { urls = new[] { url } }))).GetProperty("files")[0];
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.GetProperty("hash").GetString());
        var channel = $"cdn-{stamp}";
        var release = await Json(await client.PostAsJsonAsync("/console/v1/assets/releases", new
        {
            note = "Videos on the CDN", channel,
            files = new Dictionary<string, object> { ["public/bvm.mp4"] = new { hash = file.GetProperty("hash").GetString(), size = bytes.Length, mimeType = "", url } },
        }));
        var number = release.GetProperty("number").GetInt32();
        Assert.Equal(url, (await Json(await client.GetAsync($"/console/v1/assets/releases/{number}"))).GetProperty("files")[0].GetProperty("url").GetString());

        // Apps: no address in the listing; reading it is a link the CDN takes for a few minutes.
        using var anon = cdnApp.CreateClient();
        Assert.DoesNotContain("example.com", (await Json(await anon.GetAsync($"/assets/v1/channels/{channel}"))).ToString());
        var link = await Json(await anon.GetAsync($"/assets/v1/channels/{channel}/files/public/bvm.mp4?link=true"));
        Assert.StartsWith(url + "?Expires=", link.GetProperty("url").GetString());
        var fetched = await new HttpClient(new FakeCloudFront(key, "K2KEY", url, bytes)).GetByteArrayAsync(link.GetProperty("url").GetString());
        Assert.Equal(bytes, fetched);

        // It can't be removed while a channel's release has files on it.
        var removing = await client.DeleteAsync($"/console/v1/assets/cdns/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, removing.StatusCode);
        Assert.Contains("publish one without them", await removing.Content.ReadAsStringAsync());
    }
}
