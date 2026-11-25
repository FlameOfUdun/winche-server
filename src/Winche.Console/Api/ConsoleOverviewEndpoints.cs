using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Rules;
using Winche.Console.Tabs;
using Winche.Core;

namespace Winche.Console.Api;

/// <summary>
/// Project overview, the console's home: the project, each product the caller may see with a few cheap numbers, and
/// the latest changes (settings, rules and function versions), newest first.
/// </summary>
public static class ConsoleOverviewEndpoints
{
    private sealed record Stat(string Label, string Value);
    private sealed record Product(string Id, string Name, string Path, bool On, string About, IReadOnlyList<Stat> Stats);
    private sealed record Change(DateTimeOffset At, string What, string? By);

    public static IEndpointRouteBuilder MapConsoleOverviewEndpoints(this IEndpointRouteBuilder app, ConsoleOptions options)
    {
        app.MapGet("/overview", async (HttpContext http, CancellationToken ct) =>
        {
            var sp = http.RequestServices;
            var role = ConsoleRolePolicy.Highest(http.User, options);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Winche.Console.Overview");
            var products = new List<Product>();
            var project = sp.GetRequiredService<Project.ProjectSetup>().Current;
            // With project setup, a product is on once started; without, as before.
            bool On(string product, bool otherwise) => project.SetupEnabled ? project.IsStarted(product) : otherwise;
            // A product's numbers only once its tables are there (started and set up).
            bool Ready(string product) => Project.ProjectSetup.IsReady(sp, product);
            var changes = new List<Change>();

            // Each part is best-effort: one failing source leaves its numbers out, not the page.
            async Task<T?> Try<T>(string what, Func<Task<T>> read)
            {
                try { return await read(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Overview: couldn't read {What}", what);
                    return default;
                }
            }

            var rules = sp.GetService<RuleStoreFactory>();
            async Task<IReadOnlyList<RuleVersion>> RuleVersions(string subsystem)
            {
                if (rules is null) return [];
                await using var ctx = rules.CreateContext();
                return await new RuleVersionStore(ctx).ListAsync(subsystem, ct);
            }

            if (options.DatabaseTab is { } dt && role >= dt.MinRole && sp.GetService<Winche.Database.Runtime.DocumentDatabase>() is { } db)
            {
                var stats = new List<Stat>();
                if (Ready("database"))
                {
                var collections = await Try("collections", async () => (await db.ListCollectionIdsAsync("", ct: ct)).CollectionIds.Count);
                stats.Add(new("collections", collections.ToString("N0")));
                }
                var versions = dt.RulesEditor is not null ? await Try("database rules", () => RuleVersions("database")) ?? [] : [];
                if (versions.FirstOrDefault(v => v.IsActive) is { } active) stats.Add(new("rules", $"v{active.Version}"));
                stats.Add(new("history", dt.History ? "on" : "off"));
                products.Add(new("database", "Database", "/database", On("database", true), "Documents, live queries and rules", stats));
                changes.AddRange(versions.Take(5).Select(v => new Change(v.CreatedAtUtc, $"Database rules v{v.Version}{Note(v.Note)}", v.CreatedBy)));
            }

            if (options.StorageTab is { } st && role >= st.MinRole && sp.GetService<Winche.Storage.Services.FileStorage>() is not null)
            {
                var stats = new List<Stat>();
                var versions = st.RulesEditor is not null ? await Try("storage rules", () => RuleVersions("storage")) ?? [] : [];
                if (versions.FirstOrDefault(v => v.IsActive) is { } active) stats.Add(new("rules", $"v{active.Version}"));
                products.Add(new("storage", "Storage", "/storage", On("storage", true), "Files, uploads and downloads", stats));
                changes.AddRange(versions.Take(5).Select(v => new Change(v.CreatedAtUtc, $"Storage rules v{v.Version}{Note(v.Note)}", v.CreatedBy)));
            }

            if (ConsoleAppAuthEndpoints.Capability(options, role))
            {
                var stats = new List<Stat>();
                if (Ready("authentication") && sp.GetService<Winche.Auth.WincheAuthOptions>() is { } ao)
                {
                    await using var scope = sp.CreateAsyncScope();
                    if (scope.ServiceProvider.GetService<Winche.Auth.IWincheAuth>() is { } auth
                        && await Try("users", () => auth.ListUsersAsync(null, 0, 1, ct)) is { } page)
                        stats.Add(new("users", page.Total.ToString("N0")));
                    var methods = (ao.EmailPasswordSignIn is null ? 0 : 1) + (ao.EmailLinkSignIn is null ? 0 : 1) + (ao.AnonymousSignIn ? 1 : 0)
                        + ao.IdentityProviders.Count;
                    stats.Add(new("sign-in methods", methods.ToString()));
                }
                products.Add(new("authentication", "Authentication", "/authentication", On("authentication", true), "Sign-in for your apps' users", stats));
            }

            if (ConsoleFunctionsEndpoints.Capability(sp, role) && sp.GetService<Winche.Functions.FunctionStore>() is { } fstore)
            {
                var deploys = Ready("functions") ? await Try("functions", () => fstore.ListAsync(5, ct)) ?? [] : [];
                var live = deploys.FirstOrDefault();
                var stats = live is null ? new List<Stat>() : [new("functions", live.Functions.Count.ToString()), new("version", $"v{live.Version}")];
                products.Add(new("functions", "Functions", "/functions", On("functions", live is not null), "Your TypeScript, run on this server", stats));
                changes.AddRange(deploys.Select(d => new Change(d.At, $"Functions v{d.Version}{Note(d.Note)}", d.By)));
            }

            if (ConsoleAssetsEndpoints.Capability(options, role) && sp.GetService<Winche.Assets.WincheAssets>() is { } assets)
            {
                var channels = Ready("assets") ? await Try("asset channels", () => assets.ChannelsAsync(ct)) ?? [] : [];
                var releases = Ready("assets") ? await Try("asset releases", () => assets.ReleasesAsync(5, ct)) ?? [] : [];
                var stats = new List<Stat>();
                if (releases.FirstOrDefault() is { } latest) stats.Add(new("release", $"#{latest.Number}"));
                if (channels.Count > 0) stats.Add(new("channels", channels.Count.ToString()));
                var assetVersions = options.AssetsRulesEditor is not null ? await Try("assets rules", () => RuleVersions("assets")) ?? [] : [];
                if (assetVersions.FirstOrDefault(v => v.IsActive) is { } active) stats.Add(new("rules", $"v{active.Version}"));
                products.Add(new("assets", "Assets", "/assets", On("assets", releases.Count > 0), "Versioned content for your apps", stats));
                changes.AddRange(releases.Select(r => new Change(r.At, $"Assets release #{r.Number}{Note(r.Note)}", r.By)));
            }

            if (ConsoleRealtimeEndpoints.Capability(options, role) && sp.GetService<Winche.Realtime.RealtimeStore>() is { } realtime)
            {
                var stats = new List<Stat>();
                // Open lists, up to a hundred counted ("100+" past that): a quick look, not a census.
                var open = Ready("realtime") ? await Try("realtime lists", () => realtime.ListsAsync(null, "open", null, 101, ct)) : null;
                if (open is not null) stats.Add(new("open lists", open.Count > 100 ? "100+" : open.Count.ToString()));
                var realtimeVersions = options.RealtimeRulesEditor is not null ? await Try("realtime rules", () => RuleVersions("realtime")) ?? [] : [];
                if (realtimeVersions.FirstOrDefault(v => v.IsActive) is { } active) stats.Add(new("rules", $"v{active.Version}"));
                products.Add(new("realtime", "Realtime", "/realtime", On("realtime", open is { Count: > 0 }), "Live lists, channels and presence for your apps", stats));
            }

            if (role >= ConsoleRole.Admin && sp.GetService<SettingsStore>() is { } store
                && await Try("settings history", () => store.HistoryAsync(null, 10, ct)) is { } history)
            {
                changes.AddRange(history.Select(v => new Change(v.At,
                    $"{store.Area(v.Area)?.Title ?? v.Area} settings{(v.Reason is { Length: > 0 } r ? $": {r}" : "")}", v.By)));
            }

            return Results.Json(new
            {
                project = new { id = project.Id, name = project.Name, created = project.Created, setup = project.SetupEnabled },
                products = products.Select(p => new
                {
                    id = p.Id, name = p.Name, path = p.Path, on = p.On, about = p.About,
                    stats = p.Stats.Select(s => new { label = s.Label, value = s.Value }),
                }),
                changes = changes.OrderByDescending(c => c.At).Take(8).Select(c => new { at = c.At, what = c.What, by = c.By }),
            });
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);
        return app;
    }

    private static string Note(string? note) => string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}";
}
