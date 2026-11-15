using System.Net;
using Winche.Auth;
using Winche.Auth.Email;

namespace Winche.Console.Email;

/// <summary>
/// Console emails (admin invites, password resets) sent through the SMTP server set up for Winche Auth
/// (Authentication → Settings → Email), so a host needs no email code of its own (<c>o.UseWincheAuthEmail()</c>).
/// </summary>
public sealed class WincheAuthConsoleEmail(Func<AuthEmailOptions?> sending, IServiceProvider services, string where = "Authentication → Settings → Email")
    : IConsoleEmailSender, IConsoleEmailReadiness
{
    /// <summary>Through Winche Auth's email (Authentication → Settings → Email).</summary>
    public WincheAuthConsoleEmail(WincheAuthOptions auth, IServiceProvider services) : this(() => auth.EmailSending, services) { }

    public bool Ready => sending() is { Smtp: not null } or { LogLinksOnly: true };

    public string WhyNotReady => sending() is null
        ? $"turn on email in {where} and choose an SMTP server."
        : $"choose an SMTP server in {where}.";

    private string AppName => sending() is { AppName.Length: > 0 } e ? e.AppName : "Winche";

    public Task SendPasswordResetAsync(ConsoleEmailRecipient user, string resetLink, CancellationToken ct = default) =>
        SendAsync(user, "consoleResetPassword", $"{AppName} console: reset your password",
            $"Someone asked to reset the password for your {AppName} console account. Choose a new one here:", "Choose a new password", resetLink,
            "If it wasn't you, ignore this email: your password stays the same.", ct);

    public Task SendInviteAsync(ConsoleEmailRecipient user, string inviteLink, CancellationToken ct = default) =>
        SendAsync(user, "consoleInvite", $"You're invited to the {AppName} console",
            $"You've been invited to the {AppName} console. Set up your account here:", "Accept the invite", inviteLink,
            "The link works once and expires; ask for a new one if it has.", ct);

    private async Task SendAsync(ConsoleEmailRecipient user, string kind, string subject, string intro, string button, string link, string outro, CancellationToken ct)
    {
        var email = sending() ?? throw new InvalidOperationException(WhyNotReady);
        var hello = string.IsNullOrWhiteSpace(user.FirstName) ? "Hello," : $"Hello {user.FirstName},";
        var text = $"{hello}\n\n{intro}\n\n{link}\n\n{outro}";
        var html = $"<p>{WebUtility.HtmlEncode(hello)}</p><p>{WebUtility.HtmlEncode(intro)}</p>" +
                   $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(button)}</a></p><p>{WebUtility.HtmlEncode(outro)}</p>";
        IAuthEmailSender sender = email.LogLinksOnly
            ? new LogOnly(services)
            : new SmtpEmailSender(email, services);
        await sender.SendAsync(new AuthEmailMessage(user.Email, kind, subject, text, html, link), ct);
    }

    private sealed class LogOnly(IServiceProvider services) : IAuthEmailSender
    {
        public Task SendAsync(AuthEmailMessage m, CancellationToken ct)
        {
            var logger = (Microsoft.Extensions.Logging.ILogger?)services.GetService(typeof(Microsoft.Extensions.Logging.ILogger<WincheAuthConsoleEmail>));
            if (logger is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "Console email to {To}: \"{Subject}\" {Link}", m.To, m.Subject, m.Link);
            return Task.CompletedTask;
        }
    }
}
