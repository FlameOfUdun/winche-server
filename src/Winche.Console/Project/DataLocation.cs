using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Console.Api;
using Winche.Console.Identity;

namespace Winche.Console.Project;

/// <summary>Where a project's data is kept.</summary>
/// <param name="Host">Its PostgreSQL server; null: the server Winche Server's own database is on.</param>
/// <param name="SuggestedDatabase">A name for a database of its own.</param>
public sealed record DataLocationInfo(string? Host, int? Port, string Database, string? Username, string? SslMode,
    long? SizeBytes, string? Version, int? MaxConnections, int? CommandTimeoutSeconds, DataPrevious? Previous, DataMoveFailure? LastMoveFailed,
    string SuggestedDatabase);

/// <summary>Where the data was before a move: kept until the move is finished.</summary>
public sealed record DataPrevious(string? Host, string Database, DateTimeOffset MovedAt);

public sealed record DataMoveFailure(string Error, DateTimeOffset At);

/// <summary>Where to move the data: a database on this server's PostgreSQL (no host), or on another.</summary>
public sealed record DataMoveTarget(string? Database, string? Host, int? Port, string? Username, string? Password, string? SslMode);

public sealed record DataMoveCheck(bool Ok, string? Problem, string? Version, bool Exists, long RowsToCopy);

/// <summary>Where a project's data is kept, and moving it (Winche Server provides it, for server admins).</summary>
public interface IWincheDataLocation
{
    Task<DataLocationInfo> GetAsync(CancellationToken ct);
    Task<DataMoveCheck> CheckAsync(DataMoveTarget target, CancellationToken ct);
    /// <summary>Starts the move; the project pauses shortly after (its requests answered with the move's progress).</summary>
    Task StartMoveAsync(DataMoveTarget target, string? by, CancellationToken ct);
    Task FinishMoveAsync(bool deleteOld, CancellationToken ct);
    Task SetPoolAsync(int? maxConnections, int? commandTimeoutSeconds, CancellationToken ct);
}

public static class DataLocationEndpoints
{
    public sealed record FinishRequest(bool DeleteOld);
    public sealed record PoolRequest(int? MaxConnections, int? CommandTimeoutSeconds);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    internal static IEndpointRouteBuilder MapConsoleDataLocationEndpoints(this IEndpointRouteBuilder app, Options.ConsoleOptions options)
    {
        var group = app.MapGroup("/api/data-location").RequireAuthorization(ConsoleRoles.AdminPolicy);
        IResult? Refuse(HttpContext http) => options.DataLocation is null ? Results.NotFound()
            : !WincheProjectsCaller.Of(http.User).ServerAdmin
                ? Results.Json(new { error = "Only server admins see where data is kept." }, statusCode: StatusCodes.Status403Forbidden)
                : null;
        IResult Problem(string message) => ConsoleErrors.Problem("data-location", message, null);

        group.MapGet("", async (HttpContext http, CancellationToken ct) =>
            Refuse(http) ?? Results.Json(await options.DataLocation!.GetAsync(ct)));

        group.MapPost("/check", async (DataMoveTarget body, HttpContext http, CancellationToken ct) =>
            Refuse(http) ?? Results.Json(await options.DataLocation!.CheckAsync(body, ct)));

        group.MapPost("/move", async (DataMoveTarget body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http) is { } refused) return refused;
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } twoFactor) return twoFactor;
            try { await options.DataLocation!.StartMoveAsync(body, Who(http.User), ct); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            return Results.Accepted();
        });

        group.MapPost("/finish", async (FinishRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http) is { } refused) return refused;
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } twoFactor) return twoFactor;
            await options.DataLocation!.FinishMoveAsync(body.DeleteOld, ct);
            return Results.NoContent();
        });

        group.MapPut("/pool", async (PoolRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http) is { } refused) return refused;
            try { await options.DataLocation!.SetPoolAsync(body.MaxConnections, body.CommandTimeoutSeconds, ct); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            return Results.NoContent();
        });
        return app;
    }
}
