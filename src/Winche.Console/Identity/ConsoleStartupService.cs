using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Console.Options;

namespace Winche.Console.Identity;

/// <summary>On startup: the console's tables, and the first admin when one is configured and there are no accounts yet.</summary>
internal sealed class ConsoleStartupService(IServiceProvider services, ConsoleOptions options, Microsoft.Extensions.Logging.ILogger<ConsoleStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<ConsoleStore>().EnsureAsync(ct);

        var accounts = sp.GetRequiredService<ConsoleAccounts>();
        var any = options.Accounts is not null && await accounts.AnyAsync(ct);
        if (!any && !string.IsNullOrWhiteSpace(options.SeedAdminEmail) && !string.IsNullOrWhiteSpace(options.SeedAdminPassword))
        {
            await accounts.CreateAsync(options.SeedAdminEmail, options.SeedAdminPassword, null, null, ConsoleRoles.Admin, ct: ct);
            any = true;
        }

        // Not set up yet: the code first-run setup asks for, where only those who run the server see it.
        if (options.SetupCode is { } setupCode && !any)
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger,
                "Setup code: {Code}. Open the console and enter it to set up this server (it's asked for until someone has).", await setupCode(ct));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
