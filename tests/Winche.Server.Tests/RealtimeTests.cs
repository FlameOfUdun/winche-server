using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Server.Tests;

/// <summary>Winche Realtime as a project's product: off until started, its rules from the console, signed in as the app's users.</summary>
[Collection("postgres")]
public class RealtimeTests(PostgresFixture pg)
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_project_starts_realtime_and_its_app_users_append_and_follow_lists()
    {
        var files = Directory.CreateTempSubdirectory("winche-server-files-").FullName;
        await using var server = new ServerFactory(await pg.NewDatabaseAsync(), files);
        var console = server.CreateClient();
        await console.PostAsJsonAsync("/console/v1/session/setup", new { setupCode = ServerFactory.SetupCode, email = "admin@example.com", firstName = "Root", lastName = "Admin", password = "Passw0rd!long" });
        await console.PostAsJsonAsync("/console/v1/session/login", new { email = "admin@example.com", password = "Passw0rd!long" });
        await TestProjects.CreateAsync(console, "clinic", "Clinic", "authentication");
        var app = server.CreateClient();
        var token = (await Json(await app.PostAsJsonAsync("/projects/clinic/auth/v1/signUp", new { email = "ann@example.com", password = "correct horse battery" })))
            .GetProperty("accessToken").GetString()!;
        var uid = (await Json(await app.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/projects/clinic/auth/v1/me") { Headers = { { "Authorization", $"Bearer {token}" } } })))
            .GetProperty("uid").GetString()!;

        // Off until started: the socket refuses.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(server, token));

        await TestProjects.StartAsync(console, "clinic", "realtime");
        var parsed = await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/text/parse", new
        {
            text = "match sessions/{s} { allow get: if request.auth != null; allow create: if request.resource.device == request.auth.uid; }",
        }));
        await Json(await console.PostAsJsonAsync("/console/v1/projects/clinic/rules/realtime", new { rulesJson = parsed.GetProperty("rulesJson").GetString(), note = "Sessions" }));

        using var socket = await ConnectAsync(server, token);
        var frames = new Frames(socket);
        await frames.WaitAsync(f => (string?)f["type"] == "welcome");
        var sub = await frames.RequestAsync(new() { ["type"] = "subscribe", ["path"] = "sessions/s1" });
        var subscription = (string)sub["result"]!["subscription"]!;
        var appended = await frames.RequestAsync(new()
        {
            ["type"] = "append", ["path"] = "sessions/s1",
            ["items"] = new JsonArray(new JsonObject { ["key"] = $"{uid}:1", ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("start")) }),
        });
        Assert.Equal(1L, (long)appended["result"]!["positions"]![0]!);
        var items = await frames.WaitAsync(f => (string?)f["type"] == "items" && (string?)f["subscription"] == subscription);
        Assert.Equal(uid, (string?)items["items"]![0]!["by"]);

        // The project's rules decide: another device's key is refused.
        var refused = await frames.RequestAsync(new()
        {
            ["type"] = "append", ["path"] = "sessions/s1",
            ["items"] = new JsonArray(new JsonObject { ["key"] = "someone-else:1", ["payload"] = Convert.ToBase64String([1]) }),
        });
        Assert.Equal("permission-denied", (string?)refused["error"]!["code"]);

        // Integrity can be enforced on it like the other products.
        var integrity = await Json(await console.GetAsync("/console/v1/projects/clinic/integrity"));
        Assert.Contains("realtime", integrity.GetRawText());
    }

    private static async Task<WebSocket> ConnectAsync(ServerFactory server, string token) =>
        await server.Server.CreateWebSocketClient().ConnectAsync(new Uri(server.Server.BaseAddress, $"/projects/clinic/realtime/v1/ws?access_token={token}"), CancellationToken.None);

    /// <summary>Frames on a socket: send a request, wait for what matches.</summary>
    private sealed class Frames
    {
        private readonly WebSocket _socket;
        private readonly List<JsonObject> _inbox = [];
        private readonly SemaphoreSlim _signal = new(0);
        private int _next;

        public Frames(WebSocket socket)
        {
            _socket = socket;
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1 << 20];
                try
                {
                    while (true)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult r;
                        do { r = await _socket.ReceiveAsync(buffer, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; message.Write(buffer, 0, r.Count); }
                        while (!r.EndOfMessage);
                        lock (_inbox) _inbox.Add(JsonNode.Parse(message.ToArray())!.AsObject());
                        _signal.Release();
                    }
                }
                catch (Exception) { }
            });
        }

        public async Task<JsonObject> RequestAsync(JsonObject frame)
        {
            var id = $"r{Interlocked.Increment(ref _next)}";
            frame["id"] = id;
            await _socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            return await WaitAsync(f => (string?)f["id"] == id && (string?)f["type"] is "response" or "error");
        }

        public async Task<JsonObject> WaitAsync(Func<JsonObject, bool> match)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                lock (_inbox) { var hit = _inbox.FirstOrDefault(match); if (hit is not null) { _inbox.Remove(hit); return hit; } }
                var left = until - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero, "timed out waiting for a frame");
                await _signal.WaitAsync(left);
            }
        }
    }
}
