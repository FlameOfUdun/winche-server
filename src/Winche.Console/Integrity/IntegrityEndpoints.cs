using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Console.Api;
using Winche.Console.Identity;
using Winche.Console.Project;
using Winche.Core;
using Winche.Integrity;

namespace Winche.Console.Integrity;

/// <summary>The console's Integrity page: the apps, enforcement per product, debug tokens and the counts.</summary>
public static class ConsoleIntegrityEndpoints
{
    // ---- The console's side ----

    public sealed record AppRequest(string? Name, string? AndroidPackage, string? AppleBundleId, string? AppleTeamId, string? Id = null, bool? AppleDevelopment = null,
        long? AndroidCloudProjectNumber = null, bool? AndroidSideloaded = null, string? AndroidDeviceIntegrity = null, string[]? AndroidCertificates = null,
        string? WebSiteKey = null, string[]? WebHostnames = null);
    /// <summary>The Google service account's key file (its JSON), or a reference to where it's kept (aws-sm:…).</summary>
    public sealed record ServiceAccountRequest(string? Value, string? Reference);
    public sealed record EnforcementRequest(Dictionary<string, string>? Enforcement, int? TokenMinutes, string? Reason);
    public sealed record DebugTokenRequest(string? App, string? Name);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;
    private static IResult Problem(string message) => ConsoleErrors.Problem("integrity", message, null);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private const string NotAvailable = "Integrity isn't available on this server.";

    private static object Counts(IEnumerable<IntegrityUsage.Usage> rows) => rows.ToDictionary(u => u.Product, u => new { today = u.Today, last7Days = u.Last7Days, lastSeen = u.LastSeen });

    private static object Body(WincheIntegrity integrity, IReadOnlyList<IntegrityUsage.Usage> usage, IReadOnlyDictionary<string, (long Keys, long Development, DateTimeOffset? LastUsed)> keys,
        object? playServiceAccount, IReadOnlyDictionary<string, object?> turnstileSecrets, bool editable)
    {
        var s = integrity.Current;
        return new
        {
            available = integrity.Available, canSign = integrity.CanSign, editable,
            products = IntegritySettings.Products,
            enforcement = IntegritySettings.Products.ToDictionary(p => p, s.ModeOf),
            tokenMinutes = s.TokenMinutes,
            apps = s.Apps.Select(a => new
            {
                id = a.Id, name = a.Name, androidPackage = a.AndroidPackage, appleBundleId = a.AppleBundleId, appleTeamId = a.AppleTeamId,
                appleDevelopment = a.AppleDevelopment, androidCloudProjectNumber = a.AndroidCloudProjectNumber, androidSideloaded = a.AndroidSideloaded,
                androidDeviceIntegrity = a.AndroidDeviceIntegrity, androidCertificates = a.AndroidCertificates,
                webSiteKey = a.WebSiteKey, webHostnames = a.WebHostnames, turnstileSecret = turnstileSecrets.GetValueOrDefault(a.Id), createdAt = a.CreatedAt, usage = Counts(usage.Where(u => u.AppId == a.Id)),
                // Devices that attested a key (App Attest): how many, development builds among them, last proof.
                devices = keys.TryGetValue(a.Id, out var k) ? new { count = k.Keys, development = k.Development, lastUsed = k.LastUsed } : null,
            }),
            unverified = Counts(usage.Where(u => u.AppId == IntegrityUsage.Unverified)),
            // The Google service account Play Integrity signs in as: when it was set, and its address (never its key).
            playServiceAccount,
            debugTokens = s.DebugTokens.Select(t => new { id = t.Id, app = t.App, name = t.Name, createdAt = t.CreatedAt, createdBy = t.CreatedBy }),
        };
    }

    private static IntegrityApp From(AppRequest body, IntegrityApp? old) => (old ?? new IntegrityApp()) with
    {
        Name = body.Name?.Trim() ?? "",
        AndroidPackage = Blank(body.AndroidPackage),
        AppleBundleId = Blank(body.AppleBundleId),
        AppleTeamId = Blank(body.AppleTeamId)?.ToUpperInvariant(),
        AppleDevelopment = body.AppleDevelopment ?? old?.AppleDevelopment ?? false,
        AndroidCloudProjectNumber = body.AndroidCloudProjectNumber,
        AndroidSideloaded = body.AndroidSideloaded ?? old?.AndroidSideloaded ?? false,
        AndroidDeviceIntegrity = Blank(body.AndroidDeviceIntegrity) ?? old?.AndroidDeviceIntegrity ?? "device",
        WebSiteKey = Blank(body.WebSiteKey),
        WebHostnames = body.WebHostnames is { } hosts
            ? [.. hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct()]
            : old?.WebHostnames ?? [],
        AndroidCertificates = body.AndroidCertificates is { } certs
            ? [.. certs.Select(c => c.Trim().TrimEnd('=')).Where(c => c.Length > 0).Distinct()]
            : old?.AndroidCertificates ?? [],
    };

    /// <summary>Each app's Turnstile secret key: when it was set, and (for admins) by whom and where it's read from.</summary>
    private static async Task<IReadOnlyDictionary<string, object?>> TurnstileSecretsAsync(WincheIntegrity integrity, IServiceProvider sp, CancellationToken ct, bool admin = true)
    {
        var result = new Dictionary<string, object?>();
        if (sp.GetService<Winche.Secrets.SecretStore>() is not { Available: true } secrets) return result;
        foreach (var a in integrity.Current.Apps.Where(a => a.WebSiteKey is not null))
            if (await secrets.InfoAsync(WincheIntegrity.TurnstileSecret(a.Id), ct) is { } i)
                result[a.Id] = admin ? new { setAt = i.SetAt, setBy = i.SetBy, reference = Api.ConsoleSecretsEndpoints.ReferenceOf(i) } : new { setAt = i.SetAt };
        return result;
    }

    /// <summary>The Play service account: when it was set, and (for admins) by whom, where it's read from and its address.</summary>
    private static async Task<object?> PlayServiceAccountAsync(IServiceProvider sp, CancellationToken ct, bool admin = true)
    {
        if (sp.GetService<Winche.Secrets.SecretStore>() is not { Available: true } secrets) return null;
        if (await secrets.InfoAsync(WincheIntegrity.PlayServiceAccountSecret, ct) is not { } info) return null;
        if (!admin) return new { setAt = info.SetAt };
        string? email = null;
        try { email = Winche.Integrity.PlayIntegrity.GoogleServiceAccount.Parse(await secrets.GetAsync(WincheIntegrity.PlayServiceAccountSecret, ct))?.ClientEmail; }
        catch (Exception e) when (e is not OperationCanceledException) { }   // a reference that can't be read now: no address shown
        return new { setAt = info.SetAt, setBy = info.SetBy, reference = Api.ConsoleSecretsEndpoints.ReferenceOf(info), email };
    }

    internal static IEndpointRouteBuilder MapConsoleIntegrityEndpoints(this IEndpointRouteBuilder app, Options.ConsoleOptions options)
    {
        app.MapGet("/integrity", async (HttpContext http, WincheIntegrity integrity, IntegrityUsage usage, IntegrityStore store, CancellationToken ct) =>
        {
            var admin = Tabs.ConsoleRolePolicy.Highest(http.User, options) >= Tabs.ConsoleRole.Admin;
            // Viewers see when secrets were set, not where they're kept, who set them or the service account's address.
            var account = integrity.Available ? await PlayServiceAccountAsync(http.RequestServices, ct, admin) : null;
            var turnstile = integrity.Available ? await TurnstileSecretsAsync(integrity, http.RequestServices, ct, admin) : new Dictionary<string, object?>();
            return Results.Json(Body(integrity, integrity.Available ? await usage.ReadAsync(ct) : [],
                integrity.Available ? await store.CountKeysAsync(ct) : new Dictionary<string, (long, long, DateTimeOffset?)>(),
                account, turnstile, integrity.Available && admin));
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);

        // Every change here can open or close the project to scripts (or mint a way in): two-step sign-in when required.
        var admin = app.MapGroup("/integrity").RequireAuthorization(ConsoleRoles.AdminPolicy)
            .AddEndpointFilter(async (ctx, next) => await Api.ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(ctx.HttpContext) is { } refused ? refused : await next(ctx));

        admin.MapPost("/apps", async (AppRequest body, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            if (!integrity.Available) return Problem(NotAvailable);
            var current = integrity.Current;
            if (current.Apps.Count >= IntegritySettings.MaxApps) return Problem($"At most {IntegritySettings.MaxApps} apps.");
            var id = Blank(body.Id) ?? WincheIntegrity.NewId();
            if (!WincheIntegrity.IsValidId(id)) return Problem("An app id is 3 to 40 lower-case letters, digits and dashes, starting with a letter.");
            if (integrity.Find(id) is not null) return Problem($"There's already an app {id}.");
            var added = From(body, null) with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
            if (IntegritySettings.ProblemsOf(added).FirstOrDefault() is { } problem) return Problem(problem);
            await integrity.SaveAsync(current with { Apps = [.. current.Apps, added] }, Who(http.User), $"Added the app {added.Name}", ct);
            return Results.Json(new { id = added.Id });
        });

        admin.MapPut("/apps/{id}", async (string id, AppRequest body, HttpContext http, WincheIntegrity integrity, IntegrityStore store, CancellationToken ct) =>
        {
            var current = integrity.Current;
            if (integrity.Find(id) is not { } old) return Results.NotFound();
            var changed = From(body, old);
            if (IntegritySettings.ProblemsOf(changed).FirstOrDefault() is { } problem) return Problem(problem);
            await integrity.SaveAsync(current with { Apps = [.. current.Apps.Select(a => a.Id == id ? changed : a)] }, Who(http.User), $"Changed the app {changed.Name}", ct);
            // Another Apple app (team or bundle id): its devices' keys were for the old one, so they attest again.
            if (old.AppleTeamId != changed.AppleTeamId || old.AppleBundleId != changed.AppleBundleId) await store.RemoveKeysAsync(id, ct);
            return Results.Json(new { id });
        });

        admin.MapDelete("/apps/{id}", async (string id, HttpContext http, WincheIntegrity integrity, IntegrityStore store, CancellationToken ct) =>
        {
            var current = integrity.Current;
            if (integrity.Find(id) is not { } gone) return Results.NotFound();
            var rest = current.Apps.Where(a => a.Id != id).ToList();
            // Its debug tokens go with it. The last app gone: nothing could call, so enforced products go back to monitor.
            var enforcement = rest.Count > 0 ? current.Enforcement
                : current.Enforcement.ToDictionary(e => e.Key, e => e.Value == "enforce" ? "monitor" : e.Value);
            // Its Turnstile secret key first (AWS may refuse: then nothing is removed), then the app, then its devices' keys.
            if (http.RequestServices.GetService<Winche.Secrets.SecretStore>() is { Available: true } kept)
                await kept.DeleteAsync(WincheIntegrity.TurnstileSecret(id), keepInSource: false, Api.ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct);
            await integrity.SaveAsync(current with { Apps = rest, DebugTokens = [.. current.DebugTokens.Where(t => t.App != id)], Enforcement = enforcement },
                Who(http.User), $"Removed the app {gone.Name}", ct);
            await store.RemoveKeysAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapPut("/enforcement", async (EnforcementRequest body, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            if (!integrity.Available) return Problem(NotAvailable);
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're making this change: it goes in the history.");
            var current = integrity.Current;
            var enforcement = new Dictionary<string, string>(current.Enforcement);
            foreach (var (product, mode) in body.Enforcement ?? []) enforcement[product] = mode;
            var next = current with { Enforcement = enforcement, TokenMinutes = body.TokenMinutes ?? current.TokenMinutes };
            if (next.Problems().FirstOrDefault() is { } problem) return Problem(problem);
            await integrity.SaveAsync(next, Who(http.User), body.Reason.Trim(), ct);
            return Results.Json(new { enforcement = IntegritySettings.Products.ToDictionary(p => p, next.ModeOf), tokenMinutes = next.TokenMinutes });
        });

        // The Google service account Play Integrity signs in as: its key file (checked to be one), or a reference to it.
        admin.MapPut("/play-service-account", async (ServiceAccountRequest body, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            if (!integrity.Available) return Problem(NotAvailable);
            if (http.RequestServices.GetService<Winche.Secrets.SecretStore>() is not { Available: true } secrets)
                return Problem("Secrets need the server's master key.");
            if (ConsoleSecretsEndpoints.KeepIn(body.Reference) is null && string.IsNullOrWhiteSpace(body.Value) == string.IsNullOrWhiteSpace(body.Reference))
                return Problem("Give the service account's key file (its JSON) or a reference to where it's kept.");
            if (body.Value is { Length: > 0 } json && Winche.Integrity.PlayIntegrity.GoogleServiceAccount.Parse(json) is null)
                return Problem("That isn't a Google service account's key file: in Google Cloud, IAM → Service accounts → the account → Keys → Add key → JSON.");
            if (await Api.ConsoleSecretsEndpoints.SetAsync(secrets, WincheIntegrity.PlayServiceAccountSecret, body.Value, body.Reference, http, ct) is { } refused) return refused;
            return Results.Json(await PlayServiceAccountAsync(http.RequestServices, ct));
        });

        // An app's Turnstile secret key (the browser check): the widget's secret key, or a reference to where it's kept.
        admin.MapPut("/apps/{id}/turnstile-secret", async (string id, ServiceAccountRequest body, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            if (integrity.Find(id) is not { } app) return Results.NotFound();
            if (http.RequestServices.GetService<Winche.Secrets.SecretStore>() is not { Available: true } secrets)
                return Problem("Secrets need the server's master key.");
            if (ConsoleSecretsEndpoints.KeepIn(body.Reference) is null && string.IsNullOrWhiteSpace(body.Value) == string.IsNullOrWhiteSpace(body.Reference))
                return Problem("Give the widget's secret key (Cloudflare dashboard → Turnstile → the widget) or a reference to where it's kept.");
            if (body.Value is { Length: > 0 } v && !System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^[0-9]x[A-Za-z0-9_-]{10,200}$"))
                return Problem("That isn't a Turnstile secret key (like 0x4AAAAAAA…).");
            if (await Api.ConsoleSecretsEndpoints.SetAsync(secrets, WincheIntegrity.TurnstileSecret(app.Id), body.Value?.Trim(), body.Reference, http, ct) is { } refused) return refused;
            return Results.NoContent();
        });

        admin.MapDelete("/play-service-account", async (HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<Winche.Secrets.SecretStore>() is not { Available: true } secrets) return Results.NotFound();
            if (!await secrets.DeleteAsync(WincheIntegrity.PlayServiceAccountSecret, keepInSource: false, Api.ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct)) return Results.NotFound();
            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Winche.Console.Integrity")
                .LogInformation("{Who} removed the Google service account Play Integrity signs in as.", Who(http.User));
            return Results.NoContent();
        });

        // A debug token, shown this once (only its hash is kept).
        admin.MapPost("/debug-tokens", async (DebugTokenRequest body, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            if (!integrity.Available) return Problem(NotAvailable);
            var current = integrity.Current;
            if (integrity.Find(body.App) is not { } found) return Problem("Choose the app the debug token is for.");
            var name = Blank(body.Name);
            if (name is null || name.Length > 80) return Problem("Name the debug token (where it's used, like \"Ann's simulator\" or \"CI\"), 80 characters at most.");
            if (current.DebugTokens.Count >= IntegritySettings.MaxDebugTokens) return Problem($"At most {IntegritySettings.MaxDebugTokens} debug tokens: remove some first.");
            var (token, kept) = integrity.NewDebugToken(found.Id, name, Who(http.User));
            await integrity.SaveAsync(current with { DebugTokens = [.. current.DebugTokens, kept] }, Who(http.User), $"Made the debug token {name} for {found.Name}", ct);
            return Results.Json(new { id = kept.Id, app = found.Id, name, token });
        });

        admin.MapDelete("/debug-tokens/{id}", async (string id, HttpContext http, WincheIntegrity integrity, CancellationToken ct) =>
        {
            var current = integrity.Current;
            if (current.DebugTokens.FirstOrDefault(t => t.Id == id) is not { } gone) return Results.NotFound();
            await integrity.SaveAsync(current with { DebugTokens = [.. current.DebugTokens.Where(t => t.Id != id)] }, Who(http.User), $"Removed the debug token {gone.Name}", ct);
            return Results.NoContent();
        });
        return app;
    }
}
