using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Console.Identity;
using Winche.Functions;

namespace Winche.Console.Api;

/// <summary>
/// Plugins (Functions → Plugins, <c>winche plugins …</c>): packages of functions installed under an id, with their
/// settings and secrets. Each change is a new functions version, so it shows in the version list and rolls back with
/// it. Changing one needs two-step sign-in where settings do, as deploying does: plugins run with the server's rights.
/// </summary>
public static class ConsolePluginsEndpoints
{
    public sealed record InstallRequest(string? Id, string? Package, string? Version, Dictionary<string, JsonNode?>? Settings);
    public sealed record SettingsRequest(Dictionary<string, JsonNode?>? Settings);
    public sealed record UpgradeRequest(string? Version, Dictionary<string, JsonNode?>? Settings);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult Problem(string message, IReadOnlyList<string>? problems = null) => ConsoleErrors.Problem("functions", message, problems);

    private static object Manifest(PluginManifest m) => new
    {
        title = m.Title, description = m.Description, icon = m.Icon, access = m.Access,
        settings = m.Settings.Select(s => new { name = s.Key, label = s.Value.Label, description = s.Value.Description, type = s.Value.Type,
            required = s.Value.Required, @default = s.Value.Default?.DeepClone(), options = s.Value.Options }),
        secrets = m.Secrets.Select(s => new { name = s.Key, label = s.Value.Label, description = s.Value.Description, required = s.Value.Required }),
    };

    private static object Installed(PluginInstallInfo i, FunctionsOptions options) => new
    {
        curated = options.PluginCatalog.Contains(i.Install.Package),
        id = i.Install.Id, package = i.Install.Package, version = i.Install.Version, latest = i.Latest,
        update = i.Latest is { } l && l != i.Install.Version,
        settings = i.Install.Settings, source = i.Install.Source, by = i.Install.By, at = i.Install.At,
        manifest = Manifest(i.Install.Manifest),
        // Each of its secrets as the project names it (SMS__AUTH_TOKEN), and the required ones still unset.
        secrets = i.Install.SecretNames.Select(s => new { name = s.Key, secret = s.Value }),
        secretsNeeded = i.SecretsNeeded,
        functions = i.Functions,
    };

    private static async Task<IResult> Change(Func<Task<Deployment>> change)
    {
        try
        {
            var d = await change();
            return Results.Json(new { version = d.Version, note = d.Note });
        }
        catch (FunctionDeployException ex) { return Problem(ex.Message, ex.Problems); }
        catch (FunctionsUnavailableException ex) { return Problem(ex.Message); }
        catch (HttpRequestException ex) { return Problem($"npm can't be reached ({ex.Message})."); }
    }

    /// <summary>Catalog packages looked up lately (their newest version), for ten minutes.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (PluginPackage Package, DateTimeOffset At)> Catalog = new(StringComparer.Ordinal);

    public static void MapConsolePluginsEndpoints(this RouteGroupBuilder group, ConsoleCiScope ci)
    {
        // What's installed, with each copy's newest version (npm, or the server's own folder) and the secrets it still needs.
        group.MapGet("/plugins", async (bool? updates, WincheFunctions functions, FunctionsOptions options, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            return Results.Json(new { plugins = (await functions.PluginsAsync(updates != false, ct)).Select(i => Installed(i, options)) });
        }).WithMetadata(ci);

        // The plugins this server offers (Server settings → Plugins): each one's newest version and what it is.
        group.MapGet("/plugins/catalog", async (FunctionsOptions options, PluginPackages packages, CancellationToken ct) =>
        {
            var list = await Task.WhenAll(options.PluginCatalog.Select(async name =>
            {
                try
                {
                    var p = Catalog.TryGetValue(name, out var hit) && hit.At > DateTimeOffset.UtcNow.AddMinutes(-10) ? hit.Package
                        : (Catalog[name] = (await packages.GetAsync(name, null, ct), DateTimeOffset.UtcNow)).Package;
                    return (object)new { package = name, version = p.Version, title = p.Manifest.Title, description = p.Manifest.Description, icon = p.Manifest.Icon, problem = (string?)null };
                }
                catch (Exception ex) when (ex is FunctionDeployException or HttpRequestException)
                {
                    return new { package = name, version = (string?)null, title = name, description = (string?)null, icon = (string?)null, problem = (string?)ex.Message };
                }
            }));
            return Results.Json(new { plugins = list });
        }).WithMetadata(ci);

        // A package before installing or upgrading it: its version and its winche-plugin.json.
        group.MapGet("/plugins/package", async (string? name, string? version, PluginPackages packages, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return Problem("Give the package's name.");
            try
            {
                var p = await packages.GetAsync(name.Trim(), string.IsNullOrWhiteSpace(version) ? null : version.Trim(), ct);
                return Results.Json(new { package = p.Name, version = p.Version, manifest = Manifest(p.Manifest) });
            }
            catch (FunctionDeployException ex) { return Problem(ex.Message, ex.Problems); }
            catch (HttpRequestException ex) { return Problem($"npm can't be reached ({ex.Message})."); }
        }).WithMetadata(ci);

        group.MapPost("/plugins", async (InstallRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Package)) return Problem("Give the package's name.");
            var id = body.Id?.Trim() ?? "";
            return await Change(() => functions.InstallPluginAsync(new PluginRequest(id, body.Package.Trim(),
                string.IsNullOrWhiteSpace(body.Version) ? null : body.Version.Trim(), body.Settings), Who(http.User), ct));
        }).WithMetadata(ci);

        group.MapPut("/plugins/{id}", async (string id, SettingsRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            return await Change(() => functions.ConfigurePluginAsync(id, body.Settings ?? [], Who(http.User), ct));
        }).WithMetadata(ci);

        group.MapPost("/plugins/{id}/upgrade", async (string id, UpgradeRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            return await Change(() => functions.UpgradePluginAsync(id, string.IsNullOrWhiteSpace(body.Version) ? null : body.Version.Trim(), Who(http.User), body.Settings, ct));
        }).WithMetadata(ci);

        // What it wrote stays; its secrets go too when asked (?secrets=remove).
        group.MapDelete("/plugins/{id}", async (string id, string? secrets, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            return await Change(() => functions.UninstallPluginAsync(id, Who(http.User), secrets == "remove", ct));
        }).WithMetadata(ci);
    }
}
