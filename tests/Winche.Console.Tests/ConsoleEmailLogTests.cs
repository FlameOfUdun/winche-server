using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Auth.Email;
using Winche.Console.Email;
using Xunit;

namespace Winche.Console.Tests;

/// <summary>Console emails only logged (no email server): without their links unless the host asked for them (local development).</summary>
public sealed class ConsoleEmailLogTests
{
    private sealed class Captured : ILoggerProvider, ILogger
    {
        public readonly List<string> Lines = [];
        public ILogger CreateLogger(string category) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) { lock (Lines) Lines.Add(format(state, e)); }
        public void Dispose() { }
    }

    private static async Task<List<string>> ResetWith(bool withLinks)
    {
        var log = new Captured();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(log)).BuildServiceProvider();
        var email = new AuthEmailOptions { AppName = "Clinic" }.LogLinks(withLinks);
        await new WincheAuthConsoleEmail(() => email, services).SendPasswordResetAsync(new ConsoleEmailRecipient("ann@example.com", "Ann", null), "https://console.example.com/reset#code=SECRET");
        return log.Lines;
    }

    [Fact]
    public async Task Without_links_the_log_says_it_was_not_sent_and_has_no_link()
    {
        var lines = await ResetWith(withLinks: false);
        Assert.Contains(lines, l => l.Contains("not sent") && l.Contains("ann@example.com"));
        Assert.DoesNotContain(lines, l => l.Contains("SECRET") || l.Contains("https://"));
    }

    [Fact]
    public async Task With_links_for_local_development_the_link_is_logged() =>
        Assert.Contains(await ResetWith(withLinks: true), l => l.Contains("SECRET"));
}
