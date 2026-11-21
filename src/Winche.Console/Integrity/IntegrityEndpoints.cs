using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    public sealed record AppRequest(string? Name, string? Sdk, string? AndroidPackage, string? AppleBundleId, string? AppleTeamId, string? Id = null, bool? AppleDevelopment = null);
    public sealed record EnforcementRequest(Dictionary<string, string>? Enforcement, int? TokenMinutes, string? Reason);
    public sealed record DebugTokenRequest(string? App, string? Name);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;
    private static IResult Problem(string message) => ConsoleErrors.Problem("integrity", message, null);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private const string NotAvailable = "Integrity is set up once the console runs project setup (UseProjectSetup).";

    private static object Counts(IEnumerable<IntegrityUsage.Usage> rows) => rows.ToDictionary(u => u.Product, u => new { today = u.Today, last7Days = u.Last7Days, lastSeen = u.LastSeen });

    private static object Body(WincheIntegrity integrity, IReadOnlyList<IntegrityUsage.Usage> usage, IReadOnlyDictionary<string, (long Keys, long Development, DateTimeOffset? LastUsed)> keys, bool editable)
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
                id = a.Id, name = a.Name, sdk = a.Sdk, androidPackage = a.AndroidPackage, appleBundleId = a.AppleBundleId, appleTeamId = a.AppleTeamId,
                appleDevelopment = a.AppleDevelopment, createdAt = a.CreatedAt, usage = Counts(usage.Where(u => u.AppId == a.Id)),
                // Devices that attested a key (App Attest): how many, development builds among them, last proof.
                devices = keys.TryGetValue(a.Id, out var k) ? new { count = k.Keys, development = k.Development, lastUsed = k.LastUsed } : null,
            }),
            unverified = Counts(usage.Where(u => u.AppId == IntegrityUsage.Unverified)),
            debugTokens = s.DebugTokens.Select(t => new { id = t.Id, app = t.App, name = t.Name, createdAt = t.CreatedAt, createdBy = t.CreatedBy }),
        };
    }

    private static IntegrityApp From(AppRequest body, IntegrityApp? old) => (old ?? new IntegrityApp()) with
    {
        Name = body.Name?.Trim() ?? "",
        Sdk = Blank(body.Sdk) ?? old?.Sdk ?? "flutter",
        AndroidPackage = Blank(body.AndroidPackage),
        AppleBundleId = Blank(body.AppleBundleId),
        AppleTeamId = Blank(body.AppleTeamId)?.ToUpperInvariant(),
        AppleDevelopment = body.AppleDevelopment ?? old?.AppleDevelopment ?? false,
    };

    internal static IEndpointRouteBuilder MapConsoleIntegrityEndpoints(this IEndpointRouteBuilder app, Options.ConsoleOptions options)
    {
        app.MapGet("/integrity", async (HttpContext http, WincheIntegrity integrity, IntegrityUsage usage, IntegrityStore store, CancellationToken ct) =>
        {
            var admin = Tabs.ConsoleRolePolicy.Highest(http.User, options) >= Tabs.ConsoleRole.Admin;
            return Results.Json(Body(integrity, integrity.Available ? await usage.ReadAsync(ct) : [],
                integrity.Available ? await store.CountKeysAsync(ct) : new Dictionary<string, (long, long, DateTimeOffset?)>(), integrity.Available && admin));
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);

        var admin = app.MapGroup("/integrity").RequireAuthorization(ConsoleRoles.AdminPolicy);

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
            await integrity.SaveAsync(current with { Apps = rest, DebugTokens = [.. current.DebugTokens.Where(t => t.App != id)], Enforcement = enforcement },
                Who(http.User), $"Removed the app {gone.Name}", ct);
            await store.RemoveKeysAsync(id, ct);   // its devices' keys go too
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
