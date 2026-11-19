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
using Winche.Secrets;
using Winche.Storage.Locations;

namespace Winche.Console.Api;

/// <summary>
/// The Assets page and <c>winche assets deploy</c>: channels, releases and their differences, publishing (as the
/// signed-in admin, or a CI token with the <c>assets</c> scope), and where contents are kept. Rules are the rules
/// editor's (<c>rules/assets</c>); limits are the <c>assets</c> settings area.
/// </summary>
public static class ConsoleAssetsEndpoints
{
    public sealed record HashesRequest(List<string>? Hashes);
    public sealed record PublishRequest(Dictionary<string, AssetFile>? Files, string? Note, string? Channel);
    public sealed record ChannelRequest(int? Release);
    public sealed record UrlsRequest(List<string>? Urls);
    public sealed record CdnRequest(string? BaseUrl, string? KeyPairId, int? LinkMinutes, string? PrivateKey, string? PrivateKeyReference, string? Reason);
    public sealed record CdnTestRequest(string? Url);
    public sealed record LocationRequest(ArchiveLocation? Location, string? SecretKey, string? Reason, string? SecretKeyReference = null);

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
        var group = app.MapGroup("/assets").RequireAuthorization(ConsoleRolePolicy.For(minRole));
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
                    files = r.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new { path = f.Key, hash = f.Value.Hash, size = f.Value.Size, mimeType = f.Value.MimeType, url = f.Value.Url }),
                })
                : Refused(new AssetsException("not-found", $"There's no release {number}.")))).WithMetadata(ci);

        // The files apps get on a channel now: its release's files, each with the release its content arrived in.
        group.MapGet("/channels/{name}/files", (string name, WincheAssets assets, CancellationToken ct) => Run(async () =>
        {
            var channel = (await assets.ChannelsAsync(ct)).FirstOrDefault(c => c.Name == name);
            if (channel is null || await assets.ReleaseAsync(channel.Release, ct) is not { } live)
                return Refused(new AssetsException("not-found", $"There's no channel {name}."));
            var since = live.Files.ToDictionary(f => f.Key, _ => live.Number, StringComparer.Ordinal);
            var open = new HashSet<string>(since.Keys, StringComparer.Ordinal);
            foreach (var older in (await assets.ReleasesAsync(100, ct)).Where(r => r.Number < live.Number).OrderByDescending(r => r.Number).Take(50))
            {
                if (open.Count == 0) break;
                if (await assets.ReleaseAsync(older.Number, ct) is not { } r) continue;
                foreach (var path in open.ToList())
                    if (r.Files.TryGetValue(path, out var f) && f.Hash == live.Files[path].Hash) since[path] = r.Number;
                    else open.Remove(path);
            }
            return Results.Json(new
            {
                channel = channel.Name, release = live.Number, publishedAt = live.At, note = live.Note, by = live.By, size = live.TotalSize,
                files = live.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new
                {
                    path = f.Key, hash = f.Value.Hash, size = f.Value.Size, mimeType = f.Value.MimeType, url = f.Value.Url, since = since[f.Key],
                }),
            });
        })).WithMetadata(ci);

        // A file's bytes, with the server's rights (the console's preview); one on a CDN is fetched through a signed link.
        group.MapGet("/releases/{number:int}/content/{**path}", (int number, string path, WincheAssets assets, CancellationToken ct) => Run(async () =>
            await assets.OpenAsync(number, path, ct) is { } opened
                ? Results.Stream(opened.Content, string.IsNullOrEmpty(opened.File.MimeType) ? "application/octet-stream" : opened.File.MimeType)
                : Refused(new AssetsException("not-found", $"There's no {path} in release {number}."))));

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

        // CDN files: each one's hash and size, read by the server through a signed link (once per version of the file).
        admin.MapPost("/cdn/files", (UrlsRequest body, WincheAssets assets, CancellationToken ct) => Run(async () =>
            Results.Json(new { files = await assets.InspectCdnFilesAsync(body.Urls ?? [], ct) }))).WithMetadata(ci);

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

        // CDNs releases may name files on (with UseSettings): their address, key pair, link time and private key.
        admin.MapGet("/cdns", async (WincheAssets assets, IServiceProvider sp, CancellationToken ct) =>
        {
            var secrets = sp.GetService<SecretStore>() is { Available: true } s ? s : null;
            var list = new List<object>();
            foreach (var c in assets.Cdns)
            {
                var info = secrets is null ? null : await secrets.InfoAsync(c.SecretName, ct);
                list.Add(new { c.Id, c.Kind, c.BaseUrl, c.KeyPairId, c.LinkMinutes,
                    key = info is null ? null : new { setAt = info.SetAt, setBy = info.SetBy, reference = info.Reference?.ToString() } });
            }
            return Results.Json(new
            {
                cdns = list,
                editable = sp.GetService<SettingsStore>() is not null && sp.GetService<SettingsArea<AssetCdnSettings>>() is not null,
                secretsAvailable = secrets is not null,
            }, SettingsArea.Json);
        });

        admin.MapPut("/cdns/{id}", (string id, CdnRequest body, HttpContext http, WincheAssets assets, IServiceProvider sp, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SettingsArea<AssetCdnSettings>>() is not { } area || sp.GetService<SettingsStore>() is not { } store)
                return Problem("CDNs are set in the app's code here.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why: it goes in the settings history.");
            var cdn = new AssetCdn { Id = id, BaseUrl = body.BaseUrl?.Trim() ?? "", KeyPairId = body.KeyPairId?.Trim() ?? "", LinkMinutes = body.LinkMinutes ?? 10 };
            if (!cdn.BaseUrl.EndsWith('/')) cdn = cdn with { BaseUrl = cdn.BaseUrl + "/" };
            if (cdn.Problems() is { Count: > 0 } p) return Problem(string.Join(" ", p));
            var secrets = sp.GetService<SecretStore>() is { Available: true } s ? s : null;
            var giving = !string.IsNullOrWhiteSpace(body.PrivateKey) || !string.IsNullOrWhiteSpace(body.PrivateKeyReference);
            if (giving)
            {
                if (secrets is null) return Problem("Keeping a private key needs the master key.");
                var (pem, unreadable) = await ConsoleSecretsEndpoints.KeyFromAsync(sp, body.PrivateKey, body.PrivateKeyReference, http, ct);
                if (unreadable is not null) return unreadable;
                try { using var rsa = System.Security.Cryptography.RSA.Create(); rsa.ImportFromPem(AssetCdnSigner.Pem(pem!)); }
                catch (ArgumentException) { return Problem("The private key isn't an RSA key in PEM (-----BEGIN PRIVATE KEY----- or RSA PRIVATE KEY)."); }
                if (await ConsoleSecretsEndpoints.SetAsync(secrets, cdn.SecretName, body.PrivateKey, body.PrivateKeyReference, http, ct) is { } notSet) return notSet;
            }
            else if (secrets is null || await secrets.InfoAsync(cdn.SecretName, ct) is null) return Problem("Give its private key (or where to read it from).");
            var others = area.Current.Cdns.Where(c => c.Id != id);
            var next = area.Current with { Cdns = [.. others, cdn] };
            await store.SaveAsync(AssetCdnSettings.Area, System.Text.Json.JsonSerializer.SerializeToNode(next, SettingsArea.Json)!, Who(http.User), body.Reason.Trim(), ct);
            return Results.NoContent();
        }));

        admin.MapDelete("/cdns/{id}", (string id, string? reason, HttpContext http, WincheAssets assets, IServiceProvider sp, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SettingsArea<AssetCdnSettings>>() is not { } area || sp.GetService<SettingsStore>() is not { } store)
                return Problem("CDNs are set in the app's code here.");
            if (area.Current.Cdns.FirstOrDefault(c => c.Id == id) is not { } cdn) return Refused(new AssetsException("not-found", $"There's no CDN {id}."));
            // Apps still reading a channel's release on it would get nothing.
            foreach (var channel in await assets.ChannelsAsync(ct))
                if (await assets.ReleaseAsync(channel.Release, ct) is { } r && r.Files.Values.Any(f => f.Url is { } u && cdn.Serves(u)))
                    return Problem($"Release {r.Number} on {channel.Name} has files on {id}: publish one without them first.");
            await store.SaveAsync(AssetCdnSettings.Area, System.Text.Json.JsonSerializer.SerializeToNode(area.Current with { Cdns = [.. area.Current.Cdns.Where(c => c.Id != id)] }, SettingsArea.Json)!,
                Who(http.User), string.IsNullOrWhiteSpace(reason) ? $"Removed the CDN {id}" : reason.Trim(), ct);
            if (sp.GetService<SecretStore>() is { Available: true } secrets) await secrets.DeleteAsync(cdn.SecretName, ct);
            return Results.NoContent();
        }));

        // A file on a CDN, through a link signed now: does the CDN take the key?
        admin.MapPost("/cdns/{id}/test", (string id, CdnTestRequest body, AssetCdnSigner signer, CancellationToken ct) => Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(body.Url)) return Problem("Give the address of a file on it.");
            if (signer.For(body.Url.Trim()) is not { } cdn || cdn.Id != id) return Problem($"That address isn't under {id}'s base address.");
            try
            {
                using var response = await signer.GetAsync(body.Url.Trim(), HttpMethod.Head, ct);
                return Results.Json(new
                {
                    ok = response.IsSuccessStatusCode, status = (int)response.StatusCode,
                    message = response.IsSuccessStatusCode ? "The CDN took the signed link." : response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "The CDN refused the signed link: check the key pair id, and that the distribution trusts its key group." : $"The CDN answered {(int)response.StatusCode}.",
                });
            }
            catch (HttpRequestException ex) { return Results.Json(new { ok = false, status = 0, message = $"The CDN can't be reached ({ex.Message})." }); }
        }));

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

        admin.MapPost("/location/test", (LocationRequest body, HttpContext http, AssetMoves moves, CancellationToken ct) => Run(async () =>
        {
            if (body.Location is null) return Problem("Fill in the location.");
            var (key, unreadable) = await ConsoleSecretsEndpoints.KeyFromAsync(http.RequestServices, body.SecretKey, body.SecretKeyReference, http, ct);
            if (unreadable is not null) return unreadable;
            var steps = await moves.TestAsync(WithId(body.Location), key, ct);
            return Results.Json(new { ok = steps.All(s => s.Error is null), steps = steps.Select(s => new { name = s.Step, ok = s.Error is null, error = s.Error }) });
        }));

        admin.MapPost("/location/move", (LocationRequest body, HttpContext http, AssetMoves moves, CancellationToken ct) => Run(async () =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (body.Location is null) return Problem("Fill in the location.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're moving the assets: it goes in the history.");
            var location = WithId(body.Location);
            if (await ServerCredentialsRefusedAsync(location, http) is { } noKey) return noKey;
            var (secret, unreadable) = await ConsoleSecretsEndpoints.KeyFromAsync(http.RequestServices, body.SecretKey, body.SecretKeyReference, http, ct);
            if (unreadable is not null) return unreadable;
            if (location.Kind != "code" && (await moves.TestAsync(location, secret, ct)).FirstOrDefault(s => s.Error is not null) is { Step: not null } failed && failed.Error is not null)
                return Problem($"The new location didn't pass its test: {failed.Step}: {failed.Error}");
            await moves.StartAsync(location, secret, Who(http.User), body.Reason.Trim(), ct);
            // A key read from elsewhere is kept as its reference (replacing the value just saved): rotations there are followed.
            if (await ConsoleSecretsEndpoints.KeepKeyReferenceAsync(http.RequestServices, location.SecretName, body.SecretKeyReference, http, ct) is { } notKept) return notKept;
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
