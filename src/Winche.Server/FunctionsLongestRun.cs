using Microsoft.Extensions.Hosting;
using Winche.Functions;

namespace Winche.Server;

/// <summary>
/// Keeps a project's longest function run in step with the server setting, without restarting it (that would end the
/// runs in progress): read again at every run and deploy.
/// </summary>
internal sealed class FunctionsLongestRun(ServerSettingsProvider settings, FunctionsOptions functions) : IHostedService
{
    private void Apply(ServerSettings s) => functions.MaxTimeout = TimeSpan.FromMinutes(s.FunctionsMaxMinutes);

    public Task StartAsync(CancellationToken ct)
    {
        settings.Area.Changed += Apply;
        Apply(settings.Current);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        settings.Area.Changed -= Apply;
        return Task.CompletedTask;
    }
}
