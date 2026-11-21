using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Winche.Console.Api;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Core;

namespace Winche.Console.Project;

/// <summary>The products a project can start: each is off until someone starts it (with <c>UseProjectSetup()</c>).</summary>
public static class WincheProducts
{
    public const string Database = "database", Storage = "storage", Authentication = "authentication", Functions = "functions", Assets = "assets";

    /// <summary>Hooks were a product before each product got its own: kept in older saved projects, ignored.</summary>
    internal const string FormerHooks = "hooks";

    public static readonly string[] All = [Database, Storage, Authentication, Functions, Assets];

    /// <summary>The public API each product answers on (refused while the product isn't started). Hooks have none.</summary>
    internal static readonly (string Prefix, string Product)[] Paths =
    [
        ("/documents/v1", Database), ("/files/v1", Storage), ("/auth/v1", Authentication), ("/functions/v1", Functions), ("/assets/v1", Assets),
        // Winche Auth's signing keys and discovery: none until Authentication is set up.
        ("/.well-known/jwks.json", Authentication), ("/.well-known/openid-configuration", Authentication),
    ];
}

/// <summary>
/// The project, as the console's setup made it (settings area <c>project</c>): whether it exists yet, its name and id,
/// and which products have been started.
/// </summary>
public sealed record ProjectSettings
{
    public const string Area = "project";
    public bool Created { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Products { get; init; } = [];
    public DateTimeOffset? CreatedAt { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Created && string.IsNullOrWhiteSpace(Name)) problems.Add("Name the project.");
        if (Created && !ProjectSetup.IsValidId(Id)) problems.Add("The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.");
        if (Products.FirstOrDefault(p => !WincheProducts.All.Contains(p) && p != WincheProducts.FormerHooks) is { } unknown) problems.Add($"'{unknown}' isn't a product.");
        return problems;
    }
}

/// <summary>The project as it stands, for the console, the gate and <c>/.well-known/winche</c>.</summary>
public sealed record ProjectInfo(bool SetupEnabled, bool Created, string Id, string Name, IReadOnlyList<string> Products)
{
    public bool IsStarted(string product) => Products.Contains(product);
}

/// <summary>
/// Reads and changes the project. Without <c>UseProjectSetup()</c> (or without <c>AddWincheConfig</c>, where settings
/// live) there's no setup: the project is the one in the console's options, with every product on, as before.
/// </summary>
public sealed class ProjectSetup(ConsoleOptions options, IServiceProvider services)
{
    private SettingsArea<ProjectSettings>? Area => options.ProjectSetup ? services.GetService<SettingsArea<ProjectSettings>>() : null;

    /// <summary>Whether this console runs the setup (projects are created, products started one by one).</summary>
    public bool Enabled => Area is not null && services.GetService<SettingsStore>() is not null;

    /// <summary>
    /// Whether a product's tables are there to read: started and set up (always, without project setup). The console
    /// reads nothing of a product before then.
    /// </summary>
    public static bool IsReady(IServiceProvider sp, string product) =>
        sp.GetService<ProjectSetup>() is not { Enabled: true } setup
        || (setup.Current.IsStarted(product) && sp.GetService<ProductSetupRunner>()?.IsReady(product) != false);

    public ProjectInfo Current
    {
        get
        {
            if (!Enabled) return new(false, true, options.ProjectId, options.ProjectName ?? options.ProjectId, WincheProducts.All);
            var s = Area!.Current;
            return s.Created
                ? new(true, true, s.Id, s.Name, s.Products)
                : new(true, false, options.ProjectId, options.ProjectName ?? options.ProjectId, []);
        }
    }

    public static bool IsValidId(string? id) => WincheIds.IsValid(id);

    private async Task SaveAsync(ProjectSettings next, string? by, string reason, CancellationToken ct)
    {
        var store = services.GetRequiredService<SettingsStore>();
        await store.SaveAsync(ProjectSettings.Area, JsonSerializer.SerializeToNode(next, SettingsArea.Json)!, by, reason, ct);
    }

    /// <summary>Creates the project with <paramref name="products"/> started.</summary>
    public async Task CreateAsync(string name, string id, IReadOnlyList<string> products, string? by, CancellationToken ct)
    {
        if (Current.Created) throw new InvalidOperationException("The project already exists.");
        await SaveAsync(new ProjectSettings
        {
            Created = true, Name = name.Trim(), Id = id.Trim(), Products = [.. products.Distinct()], CreatedAt = DateTimeOffset.UtcNow,
        }, by, $"Created the project {name.Trim()}", ct);
    }

    /// <summary>Starts <paramref name="product"/> (nothing happens if it already runs).</summary>
    public async Task StartAsync(string product, string? by, CancellationToken ct)
    {
        var s = Area!.Current;
        if (!s.Created) throw new InvalidOperationException("Create the project first.");
        if (s.Products.Contains(product)) return;
        await SaveAsync(s with { Products = [.. s.Products, product] }, by, $"Started {product}", ct);
    }

    /// <summary>Renames the project (its id too).</summary>
    public async Task RenameAsync(string name, string id, string? by, CancellationToken ct)
    {
        var s = Area!.Current;
        if (!s.Created) throw new InvalidOperationException("Create the project first.");
        await SaveAsync(s with { Name = name.Trim(), Id = id.Trim() }, by, "Renamed the project", ct);
    }

    /// <summary>
    /// A console that was already in use before setup existed keeps working: if the project was never saved but the
    /// console has members or published rules, it becomes a project (from the options) with every product on.
    /// </summary>
    internal async Task CarryOverAsync(CancellationToken ct)
    {
        if (!Enabled || !options.ProjectCarryOver || Area!.Version is not null) return;
        var store = services.GetRequiredService<SettingsStore>();
        if ((await store.HistoryAsync(ProjectSettings.Area, 1, ct)).Count > 0) return;
        var inUse = false;
        await using (var scope = services.CreateAsyncScope())
        {
            if (options.Accounts is not null && scope.ServiceProvider.GetService<ConsoleAccounts>() is { } accounts)
                inUse = await accounts.AnyAsync(ct);
            if (!inUse && scope.ServiceProvider.GetService<Rules.RuleStoreFactory>() is { } rules)
            {
                await using var ctx = rules.CreateContext();
                inUse = ctx.RuleVersions.Any();
            }
        }
        if (!inUse) return;
        await SaveAsync(new ProjectSettings
        {
            Created = true, Id = options.ProjectId, Name = options.ProjectName ?? options.ProjectId, Products = WincheProducts.All,
            CreatedAt = DateTimeOffset.UtcNow,
        }, "winche", "Carried over: this console was in use before project setup", ct);
    }
}

/// <summary>Runs <see cref="ProjectSetup.CarryOverAsync"/> once at startup.</summary>
internal sealed class ProjectStartupService(ProjectSetup setup, ProductSetupRunner products, IServiceProvider services, ILogger<ProjectStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await setup.CarryOverAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Couldn't check whether to carry the project over."); }
        // The console's own log tables, then the started products, set up before requests come (where products are set up on demand).
        if (products.OnDemand && services.GetService<Logs.ConsoleLogStore>() is { } logs) await logs.EnsureCreatedAsync(ct);
        await products.SetUpStartedAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class ProjectEndpoints
{
    /// <param name="Database">Ignored (a server places a project's data when it creates it).</param>
    public sealed record CreateRequest(string? Name, string? Id, string[]? Products, DataMoveTarget? Database = null);
    public sealed record RenameRequest(string? Name, string? Id);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;
    private static IResult Problem(string message) => ConsoleErrors.Problem("project", message, null);

    private static object Body(ProjectInfo p, DateTimeOffset? image, ProductSetupRunner runner) => new
    {
        setup = p.SetupEnabled, created = p.Created, id = p.Id, name = p.Name,
        // When the thumbnail last changed (null: none): GET project/image?v=<it>.
        imageVersion = image?.ToUnixTimeMilliseconds().ToString(),
        // Each product: started, and its setup when one has run (running, failed or done).
        products = WincheProducts.All.Select(id => new { id, started = p.IsStarted(id), ready = p.IsStarted(id) && runner.IsReady(id), setup = runner.RunOf(id) }),
    };

    internal static IEndpointRouteBuilder MapConsoleProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/project", async (ProjectSetup setup, ProjectImageStore images, ProductSetupRunner runner, CancellationToken ct) =>
                Results.Json(Body(setup.Current, await images.VersionAsync(ct), runner)))
            .RequireAuthorization(ConsoleRoles.ViewerPolicy);

        var admin = app.MapGroup("/project").RequireAuthorization(ConsoleRoles.AdminPolicy);

        admin.MapPost("", async (CreateRequest body, HttpContext http, ProjectSetup setup, ProjectImageStore images, ProductSetupRunner runner, CancellationToken ct) =>
        {
            if (!setup.Enabled) return Problem("This console has no project setup (UseProjectSetup).");
            if (setup.Current.Created) return Problem("The project already exists.");
            if (string.IsNullOrWhiteSpace(body.Name)) return Problem("Name the project.");
            if (!ProjectSetup.IsValidId(body.Id)) return Problem("The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.");
            var products = (body.Products ?? []).Where(p => p != WincheProducts.FormerHooks).ToArray();
            if (products.FirstOrDefault(p => !WincheProducts.All.Contains(p)) is { } unknown) return Problem($"'{unknown}' isn't a product.");
            await setup.CreateAsync(body.Name, body.Id!, products, Who(http.User), ct);
            foreach (var product in products) await runner.SetUpNowAsync(product, ct);
            return Results.Json(Body(setup.Current, await images.VersionAsync(ct), runner));
        });

        admin.MapPut("", async (RenameRequest body, HttpContext http, ProjectSetup setup, ProjectImageStore images, ProductSetupRunner runner, CancellationToken ct) =>
        {
            if (!setup.Enabled || !setup.Current.Created) return Problem("Create the project first.");
            if (string.IsNullOrWhiteSpace(body.Name)) return Problem("Name the project.");
            if (!ProjectSetup.IsValidId(body.Id)) return Problem("The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.");
            await setup.RenameAsync(body.Name, body.Id!, Who(http.User), ct);
            return Results.Json(Body(setup.Current, await images.VersionAsync(ct), runner));
        });

        // Starts a product: its setup runs in the background (GET …/setup follows it); it's started once that's done.
        admin.MapPost("/products/{product}/start", (string product, HttpContext http, ProjectSetup setup, ProductSetupRunner runner) =>
        {
            if (!setup.Enabled || !setup.Current.Created) return Problem("Create the project first.");
            if (!WincheProducts.All.Contains(product)) return Problem($"'{product}' isn't a product.");
            if (setup.Current.IsStarted(product) && runner.IsReady(product)) return Results.Json(runner.RunOf(product) ?? new ProductSetupRun(product, "done", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            return Results.Json(runner.Start(product, Who(http.User)), statusCode: StatusCodes.Status202Accepted);
        });

        admin.MapGet("/products/{product}/setup", (string product, ProjectSetup setup, ProductSetupRunner runner) =>
        {
            if (!WincheProducts.All.Contains(product)) return Results.NotFound();
            if (runner.RunOf(product) is { } run) return Results.Json(run);
            var done = setup.Current.IsStarted(product) && runner.IsReady(product);
            return Results.Json(new ProductSetupRun(product, done ? "done" : "not-started", [], DateTimeOffset.UtcNow, null));
        });
        return app;
    }

    /// <summary>
    /// Refuses a product's public API while the product isn't started (<c>failed-precondition</c>, reason
    /// <c>service-disabled</c>). Only with <c>UseProjectSetup()</c>; put it before the products' endpoints.
    /// </summary>
    /// <param name="ungated">Addresses under a product's path that answer on their own terms (a signed link carries its
    /// own permission): neither the product's state nor the Integrity token is checked.</param>
    public static IApplicationBuilder UseWincheProjectGate(this IApplicationBuilder app, params string[] ungated) => app.Use(async (http, next) =>
    {
        var setup = http.RequestServices.GetService<ProjectSetup>();
        if (setup is { Enabled: true } && !ungated.Any(u => http.Request.Path.StartsWithSegments(u)))
        {
            var path = http.Request.Path.Value ?? "";
            foreach (var (prefix, product) in WincheProducts.Paths)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (path.Length > prefix.Length && path[prefix.Length] != '/')) continue;
                if (setup.Current.IsStarted(product) && http.RequestServices.GetService<ProductSetupRunner>() is { } runner && !runner.IsReady(product))
                {
                    var name = $"{char.ToUpperInvariant(product[0])}{product[1..]}";
                    var failed = runner.RunOf(product) is { State: "failed" } run ? run.Steps.FirstOrDefault(s => s.State == "failed") : null;
                    await (failed is not null
                        ? new WincheError(WincheErrorCode.Unavailable, $"{name} couldn't be set up on this server ({failed.Title}: {failed.Error}): retry it in the console.", "service-setup-failed")
                        : new WincheError(WincheErrorCode.Unavailable, $"{name} is being set up on this server: try again in a moment.", "service-starting"))
                        .ToResult().ExecuteAsync(http);
                    return;
                }
                if (!setup.Current.IsStarted(product))
                {
                    await new WincheError(WincheErrorCode.FailedPrecondition,
                        $"{char.ToUpperInvariant(product[0])}{product[1..]} isn't started on this server: start it in the console.", "service-disabled")
                        .ToResult().ExecuteAsync(http);
                    return;
                }
                // Integrity: the request's token checked and counted, and refused where the product is enforced.
                if (await Winche.Integrity.AspNetCore.IntegrityEndpoints.CheckAsync(http, product)) return;
                break;
            }
        }
        await next(http);
    });
}
