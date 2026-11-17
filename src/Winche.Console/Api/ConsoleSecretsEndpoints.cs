using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Identity;
using Winche.Core;
using Winche.Secrets;

namespace Winche.Console.Api;

/// <summary>
/// Where secrets can be read from besides being kept here (AWS Secrets Manager, Parameter Store, …), the project's own
/// keys for those places, and trying a reference before saving it. Every form that takes a secret (function secrets,
/// the SMTP password, a Keycloak sync's client secret, the server's) takes a value or a reference.
/// </summary>
internal static class ConsoleSecretsEndpoints
{
    public sealed record TestRequest(string? Reference);
    public sealed record KeyRequest(Dictionary<string, string?>? Values);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;
    private static IResult Problem(string message) => ConsoleErrors.Problem("secrets", message, null);

    /// <summary>
    /// Whether the caller may have references read with the app's own credentials: server admins (on Winche Server a
    /// project's admins aren't: the server's credentials could read other projects' secrets). An app with its own
    /// console has no one above its admins.
    /// </summary>
    public static bool MayUseAppCredentials(HttpContext http) => Project.WincheProjectsCaller.Of(http.User).ServerAdmin;

    /// <summary>The reference a secret is read from, as the console shows it (null for a value kept here).</summary>
    public static string? ReferenceOf(SecretInfo? info) => info?.Reference?.ToString();

    /// <summary>
    /// Sets <paramref name="name"/> from a form: a value, or a reference (checked: its source, its form, who may use the
    /// app's credentials). Null when saved; else the problem to answer with.
    /// </summary>
    public static async Task<IResult?> SetAsync(SecretStore secrets, string name, string? value, string? reference, HttpContext http, CancellationToken ct)
    {
        try { await secrets.SetFromAsync(name, value, reference, Who(http.User), MayUseAppCredentials(http), ct); return null; }
        catch (SecretReferenceException e) when (e.Code == "permission-denied") { return new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult(); }
        catch (SecretReferenceException e) { return Problem(e.Message); }
    }

    /// <summary>
    /// A bucket's secret key from a form: typed (<paramref name="value"/>) or read now through
    /// <paramref name="reference"/>, to test the location before saving. The problem to answer with when it can't be read.
    /// </summary>
    public static async Task<(string? Value, IResult? Problem)> KeyFromAsync(IServiceProvider sp, string? value, string? reference, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return (string.IsNullOrEmpty(value) ? null : value, null);
        if (!string.IsNullOrEmpty(value)) return (null, Problem("Send the secret key or where to read it from, not both."));
        if (sp.GetService<SecretStore>() is not { Available: true } secrets) return (null, Problem("Secrets need the master key."));
        if (SecretReference.Parse(reference, out var bad) is not { } r) return (null, Problem(bad!));
        try { return (await secrets.ReadReferenceAsync(r, MayUseAppCredentials(http), ct), null); }
        catch (SecretReferenceException e) when (e.Code == "permission-denied") { return (null, new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult()); }
        catch (SecretReferenceException e) { return (null, Problem(e.Message)); }
    }

    /// <summary>Keeps a location's secret key as <paramref name="reference"/> (before its move starts). Null when kept.</summary>
    public static Task<IResult?> KeepKeyReferenceAsync(IServiceProvider sp, string secretName, string? reference, HttpContext http, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reference) ? Task.FromResult<IResult?>(null)
            : SetAsync(sp.GetRequiredService<SecretStore>(), secretName, null, reference, http, ct);

    internal static RouteGroupBuilder MapConsoleSecretsEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/api/secrets").RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapGet("/sources", async (HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            var secrets = sp.GetService<SecretStore>();
            var keys = new List<object>();
            foreach (var key in (secrets?.Sources ?? []).Select(s => s.Key).OfType<SourceKey>().DistinctBy(k => k.SecretName))
            {
                var info = await secrets!.InfoAsync(key.SecretName, ct);
                keys.Add(new
                {
                    name = key.SecretName, title = key.Title, isSet = info is not null, setAt = info?.SetAt, setBy = info?.SetBy,
                    fields = key.Fields.Select(f => new { name = f.Name, title = f.Title, secret = f.Secret, required = f.Required, hint = f.Hint }),
                    sources = secrets.Sources.Where(s => s.Key?.SecretName == key.SecretName).Select(s => s.Name),
                });
            }
            return Results.Json(new
            {
                available = secrets?.Available ?? false,
                appCredentials = MayUseAppCredentials(http),
                sources = (secrets?.Sources ?? []).OrderBy(s => s.Name).Select(s => new { name = s.Name, title = s.Title, hint = s.LocatorHint, key = s.Key?.SecretName }),
                keys,
            });
        });

        // Reads a reference now: whether it works (never the value).
        group.MapPost("/test", async (TestRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (sp.GetService<SecretStore>() is not { Available: true } secrets) return Problem("Secrets need the master key.");
            if (SecretReference.Parse(body.Reference, out var problem) is not { } reference) return Problem(problem!);
            var result = await secrets.TestAsync(reference, MayUseAppCredentials(http), ct);
            return Results.Json(new { ok = result.Ok, problem = result.Problem, code = result.Code });
        });

        // The project's own key for a source (write-only, like every secret).
        group.MapPut("/keys/{name}", async (string name, KeyRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SecretStore>() is not { Available: true } secrets) return Problem("Secrets need the master key.");
            if (secrets.Sources.Select(s => s.Key).OfType<SourceKey>().FirstOrDefault(k => k.SecretName == name) is not { } key) return Results.NotFound();
            var values = body.Values ?? [];
            if (key.Problem(values) is { } problem) return Problem(problem);
            await secrets.SetAsync(name, key.ToJson(values), Who(http.User), ct);
            return Results.NoContent();
        });

        group.MapDelete("/keys/{name}", async (string name, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SecretStore>() is not { } secrets || secrets.Sources.All(s => s.Key?.SecretName != name)) return Results.NotFound();
            await secrets.DeleteAsync(name, ct);
            return Results.NoContent();
        });

        return group;
    }
}
