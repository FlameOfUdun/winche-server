using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Core;

namespace Winche.Console.Project;

/// <summary>A setup step as it stands: waiting, running, done or failed (with why).</summary>
public sealed record ProductSetupStep(string Title, string State, string? Error = null);

/// <summary>A product's setup as it stands: running, done or failed, step by step.</summary>
public sealed record ProductSetupRun(string Product, string State, IReadOnlyList<ProductSetupStep> Steps, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// Sets products up (where the host sets them up on demand, <see cref="WincheProductReadiness"/>): starting a product
/// runs its packages' setup steps in the background, which the console follows; only once they've all run is the product
/// started and its work let go. A project's started products are set up again as it starts (quick: nothing to do).
/// </summary>
public sealed class ProductSetupRunner(ProjectSetup project, IServiceProvider services, ILogger<ProductSetupRunner> logger)
{
    /// <summary>What each product needs set up, in order: those that send hooks need the delivery log first.</summary>
    private static readonly Dictionary<string, string[]> Plan = new()
    {
        [WincheProducts.Database] = [WincheProductNames.Hooks, WincheProductNames.Database],
        [WincheProducts.Storage] = [WincheProductNames.Hooks, WincheProductNames.Storage],
        [WincheProducts.Authentication] = [WincheProductNames.Hooks, WincheProductNames.Authentication],
        [WincheProducts.Functions] = [WincheProductNames.Functions],
        [WincheProducts.Assets] = [WincheProductNames.Hooks, Winche.Assets.AssetsServiceCollectionExtensions.Product],
        [WincheProducts.Realtime] = [Winche.Realtime.RealtimeProductSetup.Name],
    };

    private readonly ConcurrentDictionary<string, ProductSetupRun> _runs = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    private WincheProductReadiness? Readiness => services.GetService<WincheProductReadiness>();

    /// <summary>Whether products here are set up on demand (otherwise they set themselves up as the app starts).</summary>
    public bool OnDemand => Readiness is not null;

    private List<IWincheProductSetup> SetupsFor(string product)
    {
        var all = services.GetServices<IWincheProductSetup>().ToList();
        return [.. (Plan.TryGetValue(product, out var names) ? names : []).SelectMany(n => all.Where(s => s.Product == n))];
    }

    /// <summary>Whether <paramref name="product"/>'s packages are all set up (always, when products set themselves up).</summary>
    public bool IsReady(string product) =>
        Readiness is not { } r || SetupsFor(product).All(s => r.IsReady(s.Product));

    /// <summary>The last setup of <paramref name="product"/> since this app started, if any.</summary>
    public ProductSetupRun? RunOf(string product) => _runs.TryGetValue(product, out var run) ? run : null;

    /// <summary>
    /// Starts <paramref name="product"/>: its setup runs in the background (follow it with <see cref="RunOf"/>); once
    /// done, the product is started. Already running: that run.
    /// </summary>
    public ProductSetupRun Start(string product, string? by)
    {
        if (_runs.TryGetValue(product, out var current) && current.State == "running") return current;
        var steps = SetupsFor(product).SelectMany(s => s.Steps).Select(t => new ProductSetupStep(t, "waiting")).ToList();
        var run = new ProductSetupRun(product, "running", steps, DateTimeOffset.UtcNow, null);
        _runs[product] = run;
        _ = Task.Run(() => RunAsync(product, by, markStarted: true, CancellationToken.None));
        return run;
    }

    /// <summary>Sets <paramref name="product"/> up now, waiting for it (a project made with products already on).</summary>
    public Task SetUpNowAsync(string product, CancellationToken ct) =>
        OnDemand && Plan.ContainsKey(product) ? RunAsync(product, null, markStarted: false, ct) : Task.CompletedTask;

    /// <summary>Sets up every started product (as the project starts). A failure leaves that product waiting, not the app.</summary>
    public async Task SetUpStartedAsync(CancellationToken ct)
    {
        if (!OnDemand) return;
        foreach (var product in project.Current.Products.Where(Plan.ContainsKey))
            await RunAsync(product, null, markStarted: false, ct);
    }

    private async Task RunAsync(string product, string? by, bool markStarted, CancellationToken ct)
    {
        var setups = SetupsFor(product);
        var steps = setups.SelectMany(s => s.Steps).Select(t => new ProductSetupStep(t, "waiting")).ToList();
        var started = DateTimeOffset.UtcNow;
        void Publish(string state, DateTimeOffset? finished = null) => _runs[product] = new ProductSetupRun(product, state, [.. steps], started, finished);
        Publish("running");
        await _one.WaitAsync(ct);   // one setup at a time: products share the hooks step
        try
        {
            var index = 0;
            foreach (var setup in setups)
            {
                for (var i = 0; i < setup.Steps.Count; i++, index++)
                {
                    steps[index] = steps[index] with { State = "running" };
                    Publish("running");
                    try { await setup.RunStepAsync(i, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Setting up {Product} failed at \"{Step}\"", product, steps[index].Title);
                        steps[index] = steps[index] with { State = "failed", Error = ex.GetBaseException().Message };
                        Publish("failed", DateTimeOffset.UtcNow);
                        return;
                    }
                    steps[index] = steps[index] with { State = "done" };
                }
                Readiness?.MarkReady(setup.Product);
            }
            if (markStarted) await project.StartAsync(product, by, ct);
            Publish("done", DateTimeOffset.UtcNow);
        }
        finally { _one.Release(); }
    }
}
