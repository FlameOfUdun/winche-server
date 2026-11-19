using Microsoft.Extensions.Hosting;
using Winche.Functions;

namespace Winche.Server;

/// <summary>
/// Keeps a project's functions limits in step with the server settings (longest run, memory, runs at once, the shared
/// process's limit, processes), without restarting it (that would end the runs in progress): read again at every run
/// and deploy; the shared process's limit when it next starts.
/// </summary>
internal sealed class FunctionsLongestRun(ServerSettingsProvider settings, FunctionsOptions functions) : IHostedService
{
    private void Apply(ServerSettings s) => Apply(functions, s);

    public static void Apply(FunctionsOptions o, ServerSettings s)
    {
        o.MaxTimeout = TimeSpan.FromMinutes(s.FunctionsMaxMinutes);
        o.MaxMemoryMb = s.FunctionsMaxMemoryMb;
        o.MaxConcurrency = s.FunctionsMaxConcurrency;
        o.SharedProcessMemoryMb = s.FunctionsSharedProcessMb;
        o.MaxProcesses = s.FunctionsMaxProcesses;
        o.PluginCatalog = s.PluginCatalog;
    }

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
