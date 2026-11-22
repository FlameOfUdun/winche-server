using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Secrets;
using Winche.Console.Identity;
using Winche.Storage.Locations;

namespace Winche.Console.Api;

/// <summary>
/// Storage → Settings → Where files are kept: test a new location, then move files there as guided steps (copy,
/// switch, finish). Every step is kept in the change history with its reason.
/// </summary>
public static class ConsoleStorageLocationEndpoints
{
    public sealed record TestRequest(ArchiveLocation? Location, string? SecretKey, string? SecretKeyReference = null);
    public sealed record MoveRequest(ArchiveLocation? Location, string? SecretKey, string? Reason, string? SecretKeyReference = null);
    public sealed record StepRequest(string? Reason);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult Problem(string message) => ConsoleErrors.Problem("move", message, null);

    /// <summary>
    /// A bucket without an access key is reached with the server's own credentials (its environment's role). On a
    /// server with several projects that would give one project whatever the server can reach, so only server admins
    /// may choose one; others give the bucket a key of its own.
    /// </summary>
    private static async Task<IResult?> ServerCredentialsRefusedAsync(ArchiveLocation location, HttpContext http)
    {
        if (location is not { Kind: "s3", S3: { } s3 } || !string.IsNullOrWhiteSpace(s3.AccessKey)) return null;
        var options = http.RequestServices.GetRequiredService<Options.ConsoleOptions>();
        if (options.ServerSettings is null) return null;   // one app, its own credentials
        var me = await http.RequestServices.GetRequiredService<ConsoleAccounts>().CurrentAsync(http.User, http.RequestAborted);
        return me is { IsAdmin: true } ? null
            : Problem("Give the bucket an access key: without one it would use the server's own credentials, which only a server admin can choose.");
    }

    /// <summary>The app's own location, in words.</summary>
    internal static string DescribeCode(ArchiveLocations locations)
    {
        var archive = locations.Code;
        return archive.GetType().Name switch
        {
            "FileSystemArchive" => "On this server's disk",
            "S3Archive" => "the S3 bucket set in the app's code",
            "NullArchive" => "nowhere yet (no archive in the app's code)",
            var name => $"the app's own archive ({name})",
        };
    }

    /// <summary>A new location gets an id (it names the location's secret).</summary>
    private static ArchiveLocation WithId(ArchiveLocation l) => string.IsNullOrWhiteSpace(l.Id) ? l with { Id = Guid.NewGuid().ToString("N")[..12] } : l;

    public static IEndpointRouteBuilder MapConsoleStorageLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/location/files").RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapGet("", async (StorageMoves moves, ArchiveLocations locations, IServiceProvider sp, CancellationToken ct) =>
        {
            var state = moves.State;
            var secrets = sp.GetService<SecretStore>();
            return Results.Json(new
            {
                active = state.Active,
                activeLabel = state.Active is { } a ? a.Describe() : DescribeCode(locations),
                codeLabel = DescribeCode(locations),
                kinds = locations.Kinds,
                move = state.Move is { } m ? new
                {
                    m.Id, m.Phase, m.StartedAt, from = m.From, to = m.To,
                    fromLabel = m.From is { } f ? f.Describe() : DescribeCode(locations),
                    toLabel = m.To is { Kind: not "code" } t ? t.Describe() : DescribeCode(locations),
                } : null,
                progress = await moves.ProgressAsync(ct),
                editable = sp.GetService<SettingsStore>() is not null,
                secretsAvailable = secrets?.Available ?? false,
            }, SettingsArea.Json);
        });

        group.MapPost("/test", async (TestRequest body, HttpContext http, StorageMoves moves, CancellationToken ct) =>
        {
            if (body.Location is null) return Problem("Fill in the location.");
            if (await ServerCredentialsRefusedAsync(body.Location, http) is { } refused) return refused;
            var (key, unreadable) = await ConsoleSecretsEndpoints.KeyFromAsync(http.RequestServices, body.SecretKey, body.SecretKeyReference, http, ct);
            if (unreadable is not null) return unreadable;
            return Results.Json(await moves.TestAsync(WithId(body.Location), key, ct), SettingsArea.Json);
        });

        group.MapPost("/move", async (MoveRequest body, HttpContext http, StorageMoves moves, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (body.Location is null) return Problem("Fill in the location.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're moving the files: it goes in the history.");
            var location = WithId(body.Location);
            if (await ServerCredentialsRefusedAsync(location, http) is { } noKey) return noKey;
            var (key, unreadable) = await ConsoleSecretsEndpoints.KeyFromAsync(http.RequestServices, body.SecretKey, body.SecretKeyReference, http, ct);
            if (unreadable is not null) return unreadable;
            if (location.Kind != "code")
            {
                var test = await moves.TestAsync(location, key, ct);
                if (!test.Ok) return Problem($"The new location didn't pass its test: {string.Join(" ", test.Steps.Where(s => !s.Ok).Select(s => $"{s.Name}: {s.Error}"))}");
            }
            // A key read from elsewhere is kept as its reference: rotations there are followed. One to keep in AWS is saved
            // with the location first, then moved there.
            var keep = ConsoleSecretsEndpoints.KeepIn(body.SecretKeyReference) is not null;
            if (!keep && await ConsoleSecretsEndpoints.KeepKeyReferenceAsync(http.RequestServices, location.SecretName, body.SecretKeyReference, http, ct) is { } notKept) return notKept;
            object started;
            try { started = await moves.StartAsync(location, string.IsNullOrWhiteSpace(body.SecretKeyReference) || keep ? body.SecretKey : null, Who(http.User), body.Reason, ct); }
            catch (StorageMoveException ex) { return Problem(ex.Message); }
            var warning = keep ? await ConsoleSecretsEndpoints.KeepInAwsAfterAsync(http, location.SecretName, body.SecretKeyReference!, ct) : null;
            var answer = System.Text.Json.JsonSerializer.SerializeToNode(started, SettingsArea.Json)!.AsObject();
            if (warning is not null) answer["warning"] = warning;
            return Results.Json(answer);
        });

        foreach (var (step, run) in new (string, Func<StorageMoves, string?, string, CancellationToken, Task>)[]
        {
            ("switch", (m, by, why, ct) => m.SwitchAsync(by, why, ct)),
            ("finish", (m, by, why, ct) => m.FinishAsync(by, why, ct)),
            ("cancel", (m, by, why, ct) => m.CancelAsync(by, why, ct)),
        })
        {
            group.MapPost($"/move/{step}", async (StepRequest body, HttpContext http, StorageMoves moves, CancellationToken ct) =>
            {
                if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
                if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why: it goes in the history.");
                try { await run(moves, Who(http.User), body.Reason, ct); return Results.NoContent(); }
                catch (StorageMoveException ex) { return Problem(ex.Message); }
            });
        }
        return app;
    }
}
