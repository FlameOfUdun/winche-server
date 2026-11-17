using System.Net;
using System.Text.Json.Nodes;

namespace Winche.Server.Tests;

/// <summary>Amazon SES (v2 API) on localhost: keeps each SendEmail request, answers with a message id.</summary>
public sealed class FakeSes : IDisposable
{
    private readonly HttpListener _listener;
    public string Url { get; }
    public List<JsonObject> Sent { get; } = [];
    public List<string?> Authorizations { get; } = [];

    public FakeSes()
    {
        var port = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Max(e => e.Port) % 20000 + 40000;
        // A listener that fails to start is disposed (on Linux), so each port gets a new one.
        for (; ; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); _listener = listener; break; }
            catch (HttpListenerException) { listener.Close(); }
        }
        Url = $"http://127.0.0.1:{port}";
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            if (ctx.Request.Url!.AbsolutePath == "/v2/email/outbound-emails")
                lock (Sent) { Sent.Add(JsonNode.Parse(body)!.AsObject()); Authorizations.Add(ctx.Request.Headers["Authorization"]); }
            ctx.Response.ContentType = "application/json";
            var reply = """{"MessageId":"fake-1"}"""u8.ToArray();
            await ctx.Response.OutputStream.WriteAsync(reply);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Stop();
}
