using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Api;
using Winche.Console.Identity;

namespace Winche.Console.Project;

/// <summary>Who's asking for the list of projects: which ones they may see.</summary>
public sealed record WincheProjectsCaller(string? UserId, bool ServerAdmin)
{
    internal const string AccountRoleClaim = "winche:account-role";

    internal static WincheProjectsCaller Of(ClaimsPrincipal user)
    {
        var accountRoles = user.FindAll(AccountRoleClaim).Select(c => c.Value).ToList();
        // Without project members here, the role claims are the account's own.
        if (!user.HasClaim(c => c.Type == ProjectRoleClaims.DoneClaim)) accountRoles = [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value)];
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } uid ? uid : null;
        return new(id, accountRoles.Contains(ConsoleRoles.Admin));
    }
}

/// <summary>A project on the server, for the console's project switcher.</summary>
/// <param name="ConsolePath">Its console under the server's root, e.g. <c>/p/wards/_console</c>.</param>
public sealed record WincheProjectInfo(string Id, string Name, string ConsolePath, DateTimeOffset? ImageVersion)
{
    /// <summary>The products started in it (database, storage, authentication, functions); null: the host doesn't say.</summary>
    public IReadOnlyList<string>? Products { get; init; }
}

/// <summary>An archived project: stopped, its address refused, its data kept until it's restored or deleted.</summary>
public sealed record WincheArchivedProject(string Id, string Name, DateTimeOffset ArchivedAt);

/// <summary>
/// The projects on the server this console runs in, and creating one. A host that runs several projects (Winche
/// Server) registers it; without it the console knows only its own project.
/// </summary>
public interface IWincheProjects
{
    /// <summary>This console's path under the server's root: <c>/p/&lt;id&gt;</c> for a project's, empty for the server's own.</summary>
    string CurrentPrefix { get; }

    /// <summary>The projects <paramref name="caller"/> may use.</summary>
    Task<IReadOnlyList<WincheProjectInfo>> ListAsync(WincheProjectsCaller caller, CancellationToken ct);

    /// <summary>
    /// Creates a project (named, every product off) and starts it, its data in <paramref name="database"/> (none: a
    /// database of its own on the server's PostgreSQL).
    /// </summary>
    Task<WincheProjectInfo> CreateAsync(string name, string id, DataMoveTarget? database, string? by, CancellationToken ct);

    /// <summary>Whether a new project's data can go to <paramref name="database"/>: reachable, new or empty.</summary>
    Task<DataMoveCheck> CheckDatabaseAsync(string id, DataMoveTarget? database, CancellationToken ct);

    /// <summary>The archived projects (shown to server admins).</summary>
    Task<IReadOnlyList<WincheArchivedProject>> ListArchivedAsync(CancellationToken ct);

    /// <summary>Stops a project and refuses its address; its database and files stay.</summary>
    Task ArchiveAsync(string id, string? by, CancellationToken ct);

    /// <summary>Starts an archived project again.</summary>
    Task RestoreAsync(string id, CancellationToken ct);

    /// <summary>Deletes an archived project: its database and files, for good.</summary>
    Task DeleteAsync(string id, CancellationToken ct);
}

public static class WincheProjectEndpoints
{
    /// <param name="Database">Where its data goes; none: a database of its own on the server's PostgreSQL.</param>
    public sealed record CreateRequest(string? Name, string? Id, DataMoveTarget? Database = null);
    public sealed record CheckDatabaseRequest(string? Id, DataMoveTarget? Database);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    /// <summary>The server's root as seen from this request: the path base without this project's own part.</summary>
    private static string Root(HttpRequest request, IWincheProjects projects)
    {
        var pathBase = request.PathBase.Value ?? "";
        var prefix = projects.CurrentPrefix;
        return prefix.Length > 0 && pathBase.EndsWith(prefix, StringComparison.Ordinal) ? pathBase[..^prefix.Length] : pathBase;
    }

    internal static IEndpointRouteBuilder MapConsoleWincheProjectEndpoints(this IEndpointRouteBuilder app, Options.ConsoleOptions options)
    {
        app.MapGet("/api/projects", async (HttpContext http, ProjectSetup setup, CancellationToken ct) =>
        {
            var projects = http.RequestServices.GetService<IWincheProjects>();
            var caller = WincheProjectsCaller.Of(http.User);
            if (projects is null) return Results.Json(new { available = false, canCreate = false, current = setup.Current.Id, projects = Array.Empty<object>() });
            var root = Root(http.Request, projects);
            var list = await projects.ListAsync(caller, ct);
            return Results.Json(new
            {
                available = true,
                canCreate = caller.ServerAdmin,   // projects are the server's: its admins make them
                current = options.ServerConsole ? null : setup.Current.Id,   // the server's console is in none
                projects = list.Select(p => new
                {
                    id = p.Id, name = p.Name, consoleUrl = root + p.ConsolePath,
                    imageVersion = p.ImageVersion?.ToUnixTimeMilliseconds().ToString(), products = p.Products,
                }),
                archived = caller.ServerAdmin
                    ? (await projects.ListArchivedAsync(ct)).Select(a => new { id = a.Id, name = a.Name, archivedAt = a.ArchivedAt }).Cast<object>()
                    : [],
            });
        }).RequireAuthorization(ConsoleRoles.SignedInPolicy);   // the CLI signs in at the root: a project's member lists their projects there

        app.MapPost("/api/projects", async (CreateRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IWincheProjects>() is not { } projects)
                return ConsoleErrors.Problem("projects", "This console runs one project only.", null);
            if (!WincheProjectsCaller.Of(http.User).ServerAdmin)
                return Results.Json(new { error = "Only server admins create projects." }, statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(body.Name)) return ConsoleErrors.Problem("projects", "Name the project.", null);
            if (!ProjectSetup.IsValidId(body.Id))
                return ConsoleErrors.Problem("projects", "The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.", null);
            try
            {
                var created = await projects.CreateAsync(body.Name.Trim(), body.Id!, body.Database, Who(http.User), ct);
                return Results.Json(new { id = created.Id, name = created.Name, consoleUrl = Root(http.Request, projects) + created.ConsolePath });
            }
            catch (ArgumentException ex)
            {
                return ConsoleErrors.Problem("projects", ex.Message, null);
            }
        }).RequireAuthorization(ConsoleRoles.AdminPolicy);
        // Before creating: whether its data can go where the form says.
        app.MapPost("/api/projects/check-database", async (CheckDatabaseRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IWincheProjects>() is not { } projects)
                return ConsoleErrors.Problem("projects", "This console runs one project only.", null);
            if (!WincheProjectsCaller.Of(http.User).ServerAdmin)
                return Results.Json(new { error = "Only server admins create projects." }, statusCode: StatusCodes.Status403Forbidden);
            if (!ProjectSetup.IsValidId(body.Id))
                return ConsoleErrors.Problem("projects", "The project id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.", null);
            return Results.Json(await projects.CheckDatabaseAsync(body.Id!, body.Database, ct));
        }).RequireAuthorization(ConsoleRoles.AdminPolicy);
        // Archiving, restoring and deleting: server admins.
        async Task<IResult> AsServerAdmin(HttpContext http, Func<IWincheProjects, Task<IResult>> work)
        {
            if (http.RequestServices.GetService<IWincheProjects>() is not { } projects)
                return ConsoleErrors.Problem("projects", "This console runs one project only.", null);
            if (!WincheProjectsCaller.Of(http.User).ServerAdmin)
                return Results.Json(new { error = "Only server admins archive, restore or delete projects." }, statusCode: StatusCodes.Status403Forbidden);
            try { return await work(projects); }
            catch (ArgumentException ex) { return ConsoleErrors.Problem("projects", ex.Message, null); }
        }
        app.MapPost("/api/projects/{id}/archive", (string id, HttpContext http, CancellationToken ct) =>
            AsServerAdmin(http, async p => { await p.ArchiveAsync(id, Who(http.User), ct); return Results.NoContent(); })).RequireAuthorization(ConsoleRoles.AdminPolicy);
        app.MapPost("/api/projects/{id}/restore", (string id, HttpContext http, CancellationToken ct) =>
            AsServerAdmin(http, async p => { await p.RestoreAsync(id, ct); return Results.NoContent(); })).RequireAuthorization(ConsoleRoles.AdminPolicy);
        app.MapDelete("/api/projects/{id}", (string id, HttpContext http, CancellationToken ct) =>
            AsServerAdmin(http, async p => { await p.DeleteAsync(id, ct); return Results.NoContent(); })).RequireAuthorization(ConsoleRoles.AdminPolicy);
        return app;
    }
}
