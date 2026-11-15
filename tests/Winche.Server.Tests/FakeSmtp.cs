using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Winche.Server.Tests;

/// <summary>A one-connection-at-a-time SMTP server that keeps what it's sent (enough for MailKit, no TLS).</summary>
public sealed class FakeSmtp : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<(string User, string Password, string Data)> Received { get; } = [];

    public FakeSmtp()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            string user = "", password = "";
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.Split(' ')[0].ToUpperInvariant();
                if (cmd is "EHLO") { await writer.WriteLineAsync("250-fake"); await writer.WriteLineAsync("250 AUTH PLAIN"); }
                else if (cmd is "AUTH")
                {
                    var plain = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[2])).Split('\0');
                    (user, password) = (plain[1], plain[2]);
                    await writer.WriteLineAsync("235 ok");
                }
                else if (cmd is "DATA")
                {
                    await writer.WriteLineAsync("354 go");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d);
                    Received.Add((user, password, data.ToString()));
                    await writer.WriteLineAsync("250 queued");
                }
                else if (cmd is "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 ok");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

