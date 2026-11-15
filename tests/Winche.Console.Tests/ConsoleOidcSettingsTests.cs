using Xunit;
using Winche.Console.Identity;

namespace Winche.Console.Tests;

public class ConsoleOidcSettingsTests
{
    private static ConsoleOidcSettings At(string issuer) => new() { Enabled = true, Issuer = issuer, ClientId = "winche-console" };

    [Theory]
    [InlineData("https://login.example.com/realms/staff")]
    [InlineData("http://localhost:8080/realms/winche")]
    [InlineData("http://host.docker.internal:8080/realms/winche")]   // Docker's host
    [InlineData("http://keycloak:8080/realms/winche")]               // a container on the same Docker network
    public void An_issuer_is_https_or_only_reachable_locally(string issuer) => Assert.Empty(At(issuer).Problems());

    [Theory]
    [InlineData("http://login.example.com/realms/staff")]
    [InlineData("login.example.com")]
    public void A_public_issuer_over_http_is_refused(string issuer) => Assert.Contains(At(issuer).Problems(), p => p.Contains("https"));
}
