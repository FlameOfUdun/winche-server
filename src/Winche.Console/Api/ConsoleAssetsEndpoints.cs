using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Assets;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;
using Winche.Core;
using Winche.Storage.Locations;

namespace Winche.Console.Api;

/// <summary>
/// The Assets page and <c>winche assets deploy</c>: channels, releases and their differences, publishing (as the
/// signed-in admin, or a CI token with the <c>assets</c> scope), and where contents are kept. Rules are the rules
/// editor's (<c>api/rules/assets</c>); limits are the <c>assets</c> settings area.
/// </summary>
public static class ConsoleAssetsEndpoints
{
    public sealed record HashesRequest(List<string>? Hashes);
    public sealed record PublishRequest(Dictionary<string, AssetFile>? Files, string? Note, string? Channel);
    public sealed record ChannelRequest(int? Release);
    public sealed record LocationRequest(ArchiveLocation? Location, string? SecretKey, string? Reason);

    internal static bool Capability(ConsoleOptions options, ConsoleRole? role) => options.AssetsTab is { } t && role is { } r && r >= t.MinRole;

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult Problem(string message, IReadOnlyList<string>? problems = null) => ConsoleErrors.Problem("assets", message, problems);

    private static IResult Refused(AssetsException ex) =>
        new WincheError(WincheError.IsCode(ex.Code) ? WincheError.Parse(ex.Code) : WincheErrorCode.InvalidArgument, ex.Message, "assets",
            ex.Problems.Count > 0 ? new JsonObject { ["errors"] = new JsonArray([.. ex.Problems.Select(p => JsonValue.Create(p))]) } : null).ToResult();

    private static async Task<IResult> Run(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (AssetsException ex) { return Refused(ex); }
    }

    private static object Summary(AssetReleaseSummary r) => new { number = r.Number, note = r.Note, by = r.By, at = r.At, files = r.FileCount, size = r.TotalSize };

    private static object Channel(AssetChannel c) => new { name = c.Name, release = c.Release, updatedAt = c.UpdatedAt, by = c.By };

    public static IEndpointRouteBuilder MapConsoleAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ConsoleOptions>();
        var minRole = options.AssetsTab!.MinRole;
        var group = app.MapGroup("/api/assets").RequireAuthorization(ConsoleRolePolicy.For(minRole));
        // Nothing of Assets is read before it's started and set up.
        group.AddEndpointFilter(async (ctx, next) =>
            Project.ProjectSetup.IsReady(ctx.HttpContext.RequestServices, Project.WincheProducts.Assets) ? await next(ctx)
                : Problem("Start Assets first (Project → Products)."));
        var ci = new ConsoleCiScope(ConsoleCiScope.Assets);

        group.MapGet("", (WincheAssets assets, AssetMoves moves, AssetArchives archives, CancellationToken ct) => Run(async () => Results.Json(new
        {
            channels = (await assets.ChannelsAsync(ct)).Select(Channel),
            releases = (await assets.ReleasesAsync(20, ct)).Select(Summary),
            limits = assets.Limits,
            location = Label(moves.State.Active, archives),
            moving = moves.State.Move is { } m ? new { to = m.To.Describe(), m.Copied, m.Total, m.Error, m.StartedAt } : null,
        }, SettingsArea.Json))).WithMetadata(ci);

        group.MapGet("/releases", (int? limit, WincheAssets assets, CancellationToken ct) =>
            Run(async () => Results.Json(new { releases = (await assets.ReleasesAsync(Math.Clamp(limit ?? 100, 1, 500), ct)).Select(Summary) }))).WithMetadata(ci);

        group.MapGet("/releases/{number:int}", (int number, WincheAssets assets, CancellationToken ct) => Run(async () =>
            await assets.ReleaseAsync(number, ct) is { } r
                ? Results.Json(new
                {
                    number = r.Number, note = r.Note, by = r.By, at = r.At, size = r.TotalSize,
                    files = r.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new { path = f.Key, hash = f.Value.Hash, size = f.Value.Size, mimeType = f.Value.MimeType }),
                })
                : Refused(new AssetsException("not-found", $"There's no release {number}.")))).WithMetadata(ci);

        group.MapGet("/releases/{from:int}/diff/{to:int}", (int from, int to, WincheAssets assets, CancellationToken ct) =>
            Run(async () => Results.Json(await assets.DiffAsync(from, to, ct)))).WithMetadata(ci);

        group.MapGet("/channels/{name}/history", (string name, WincheAssets assets, CancellationToken ct) =>
            Run(async () => Results.Json(new { moves = await assets.HistoryAsync(name, 100, ct) }))).WithMetadata(ci);

        // Publishing and channels: admins (and CI tokens with the assets scope); two-step sign-in where settings ask.
        var admin = group.MapGroup("").RequireAuthorization(ConsoleRoles.AdminPolicy);

        admin.MapPost("/contents/missing", (HashesRequest body, WincheAssets assets, CancellationToken ct) =>
            Run(async () => Results.Json(new { missing = await assets.MissingAsync(body.Hashes ?? [], ct) }))).WithMetadata(ci);

        admin.MapPut("/contents/{hash}", (string hash, HttpContext http, WincheAssets assets, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (http.Request.ContentLength > assets.Limits.MaxFileBytes) return Problem($"A file is at most {assets.Limits.MaxFileBytes / 1024 / 1024} MB.");
            await assets.PutContentAsync(http.Request.Body, hash, ct);
            return Results.NoContent();
        })).WithMetadata(ci).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());   // the limits setting decides

        admin.MapPost("/releases", (PublishRequest body, HttpContext http, WincheAssets assets, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Note)) return Problem("Say what changed: it goes in the release list.");
            var release = await assets.PublishAsync(new AssetPublishRequest(body.Files ?? [], body.Note, string.IsNullOrWhiteSpace(body.Channel) ? null : body.Channel.Trim()), Who(http.User), ct);
            return Results.Json(new { number = release.Number, note = release.Note, by = release.By, at = release.At, files = release.Files.Count, size = release.TotalSize, channel = body.Channel });
        })).WithMetadata(ci);

        admin.MapPost("/channels/{name}", (string name, ChannelRequest body, HttpContext http, WincheAssets assets, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (body.Release is not { } release) return Problem("Say which release the channel points at.");
            return Results.Json(Channel(await assets.MoveChannelAsync(name, release, Who(http.User), ct)));
        })).WithMetadata(ci);

        admin.MapDelete("/channels/{name}", (string name, HttpContext http, WincheAssets assets, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            await assets.RemoveChannelAsync(name, ct);
            return Results.NoContent();
        })).WithMetadata(ci);

        // Where contents are kept (with UseSettings): test a place, move there, cancel. Not for CI tokens.
        admin.MapGet("/location", (AssetArchives archives, AssetMoves moves, IServiceProvider sp) => Results.Json(new
        {
            active = moves.State.Active,
            activeLabel = Label(moves.State.Active, archives),
            codeLabel = ConsoleStorageLocationEndpoints.DescribeCode(archives.Locations),
            move = moves.State.Move,
            editable = sp.GetService<SettingsStore>() is not null && sp.GetService<SettingsArea<AssetLocationSettings>>() is not null,
            secretsAvailable = sp.GetService<SecretStore>()?.Available ?? false,
        }, SettingsArea.Json));

        admin.MapPost("/location/test", (LocationRequest body, AssetMoves moves, CancellationToken ct) => Run(async () =>
        {
            if (body.Location is null) return Problem("Fill in the location.");
            var steps = await moves.TestAsync(WithId(body.Location), string.IsNullOrEmpty(body.SecretKey) ? null : body.SecretKey, ct);
            return Results.Json(new { ok = steps.All(s => s.Error is null), steps = steps.Select(s => new { name = s.Step, ok = s.Error is null, error = s.Error }) });
        }));

        admin.MapPost("/location/move", (LocationRequest body, HttpContext http, AssetMoves moves, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (body.Location is null) return Problem("Fill in the location.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're moving the assets: it goes in the history.");
            var location = WithId(body.Location);
            if (await ServerCredentialsRefusedAsync(location, http) is { } noKey) return noKey;
            var secret = string.IsNullOrEmpty(body.SecretKey) ? null : body.SecretKey;
            if (location.Kind != "code" && (await moves.TestAsync(location, secret, ct)).FirstOrDefault(s => s.Error is not null) is { Step: not null } failed && failed.Error is not null)
                return Problem($"The new location didn't pass its test: {failed.Step}: {failed.Error}");
            await moves.StartAsync(location, secret, Who(http.User), body.Reason.Trim(), ct);
            return Results.NoContent();
        }));

        admin.MapPost("/location/move/cancel", (HttpContext http, AssetMoves moves, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            await moves.CancelAsync(Who(http.User), ct);
            return Results.NoContent();
        }));
        return app;
    }

    /// <summary>Where contents are, in words: a bucket by name, else the app's own location as Storage words it.</summary>
    private static string Label(ArchiveLocation? active, AssetArchives archives) =>
        active?.Describe() ?? ConsoleStorageLocationEndpoints.DescribeCode(archives.Locations);

    private static ArchiveLocation WithId(ArchiveLocation l) => string.IsNullOrWhiteSpace(l.Id) ? l with { Id = Guid.NewGuid().ToString("N")[..12] } : l;

    /// <summary>As for files: a bucket without its own key would use the server's credentials, which only server admins choose.</summary>
    private static async Task<IResult?> ServerCredentialsRefusedAsync(ArchiveLocation location, HttpContext http)
    {
        if (location is not { Kind: "s3", S3: { } s3 } || !string.IsNullOrWhiteSpace(s3.AccessKey)) return null;
        var options = http.RequestServices.GetRequiredService<ConsoleOptions>();
        if (options.ServerSettings is null) return null;
        var me = await http.RequestServices.GetRequiredService<ConsoleAccounts>().CurrentAsync(http.User, http.RequestAborted);
        return me is { IsAdmin: true } ? null
            : Problem("Give the bucket an access key: without one it would use the server's own credentials, which only a server admin can choose.");
    }
}
