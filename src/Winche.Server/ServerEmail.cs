using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using MimeKit;
using Winche.Auth.Email;
using Winche.Console.Email;

namespace Winche.Server;

/// <summary>
/// The server's own email (<see cref="ServerEmailOptions"/>): Amazon SES with the credentials of the server's
/// environment. One client for the server; each project sends as its own app name unless a From name is set.
/// </summary>
public sealed class ServerEmail
{
    private readonly ServerEmailOptions _options;
    private readonly IAmazonSimpleEmailServiceV2 _ses;

    public ServerEmail(ServerEmailOptions options, IAmazonSimpleEmailServiceV2? client = null)
    {
        _options = options;
        _ses = client ?? new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(options.Region));   // the SDK's own credential chain
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerOptions, ServerEmail?> Made = new();

    /// <summary>The server's email (one per server), or null when the deployment sets none.</summary>
    public static ServerEmail? Of(ServerOptions server) =>
        Made.GetValue(server, s => s.Email is { Provider.Length: > 0 } e ? new ServerEmail(e) : null);

    public string Description => $"Amazon SES ({_options.Region}) as {_options.FromAddress}";

    /// <summary>A sender for one app (a project, the console's accounts), named after it.</summary>
    public IHostEmailSender For(Func<string?> appName) => new Sender(this, appName);

    internal async Task SendAsync(AuthEmailMessage message, string? appName, CancellationToken ct)
    {
        var from = new MailboxAddress(_options.FromName ?? (string.IsNullOrWhiteSpace(appName) ? "Winche" : appName), _options.FromAddress);
        await _ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = from.ToString(FormatOptions.Default, encode: true),   // the name encoded when it isn't ASCII
            Destination = new Destination { ToAddresses = [message.To] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = message.Subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Text = new Content { Data = message.Text, Charset = "UTF-8" },
                        Html = message.Html is null ? null : new Content { Data = message.Html, Charset = "UTF-8" },
                    },
                },
            },
        }, ct);
    }

    private sealed class Sender(ServerEmail email, Func<string?> appName) : IHostEmailSender
    {
        public string Description => email.Description;
        public Task SendAsync(AuthEmailMessage message, CancellationToken ct) => email.SendAsync(message, appName(), ct);
    }
}
