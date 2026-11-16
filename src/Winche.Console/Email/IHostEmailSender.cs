using Winche.Auth.Email;

namespace Winche.Console.Email;

/// <summary>
/// Email the host sends itself (the server's own account with an email service), registered as the app's
/// <see cref="IAuthEmailSender"/>: Winche Auth uses it when no SMTP server is chosen, and the console says what it is.
/// </summary>
public interface IHostEmailSender : IAuthEmailSender
{
    /// <summary>What it is, for people, e.g. "Amazon SES (eu-west-1) as no-reply@example.com".</summary>
    string Description { get; }
}
