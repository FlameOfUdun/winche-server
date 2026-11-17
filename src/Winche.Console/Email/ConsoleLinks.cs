using Microsoft.AspNetCore.Http;

namespace Winche.Console.Email;

/// <summary>
/// Where the console is, for building links: <see cref="Value"/> its pages (from the host's root, e.g. <c>/console</c>),
/// <see cref="Api"/> its API under the request's path base (set in MapWincheConsole / MapWincheConsoleApi).
/// </summary>
internal sealed class ConsolePrefix
{
    public string Value { get; set; } = "/console";
    public string Api { get; set; } = "/console/v1";
}

internal static class ConsoleLinks
{
    /// <summary>Builds an absolute reset/invite link to the SPA's reset-password page (the console's pages, from the host's root).</summary>
    public static string ResetPasswordLink(HttpContext http, string prefix, string email, string token) =>
        $"{http.Request.Scheme}://{http.Request.Host}{prefix}/reset-password" +
        $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";

    /// <summary>Builds an absolute invite link to the SPA's invite-acceptance page (the console's pages, from the host's root).</summary>
    public static string InviteLink(HttpContext http, string prefix, string token) =>
        $"{http.Request.Scheme}://{http.Request.Host}{prefix}/invite" +
        $"?token={Uri.EscapeDataString(token)}";
}
