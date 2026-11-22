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
        try
        {
            if (KeepIn(reference) is { } source)
            {
                if (string.IsNullOrEmpty(value)) return Problem("Give the value to keep in AWS.");
                await secrets.SetInSourceAsync(name, source, value, Who(http.User), MayUseAppCredentials(http), ct);
            }
            else await secrets.SetFromAsync(name, value, reference, Who(http.User), MayUseAppCredentials(http), ct);
            return null;
        }
        catch (SecretReferenceException e) when (e.Code == "permission-denied") { return new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult(); }
        catch (SecretReferenceException e) { return Problem(e.Message); }
    }

    /// <summary>A form's "keep it in AWS": the reference <c>keep:&lt;source&gt;</c> beside a value (<c>keep:aws-sm</c>), else null.</summary>
    public static string? KeepIn(string? reference) =>
        reference?.Trim() is { } r && r.StartsWith(KeepPrefix, StringComparison.Ordinal) && r.Length > KeepPrefix.Length ? r[KeepPrefix.Length..] : null;

    /// <summary>What a form sends as the reference to keep the value it sends in a source, e.g. <c>keep:aws-sm</c>.</summary>
    public const string KeepPrefix = "keep:";

    /// <summary>
    /// A bucket's secret key from a form: typed (<paramref name="value"/>) or read now through
    /// <paramref name="reference"/>, to test the location before saving. The problem to answer with when it can't be read.
    /// </summary>
    public static async Task<(string? Value, IResult? Problem)> KeyFromAsync(IServiceProvider sp, string? value, string? reference, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference) || KeepIn(reference) is not null) return (string.IsNullOrEmpty(value) ? null : value, null);   // kept in AWS once saved
        if (!string.IsNullOrEmpty(value)) return (null, Problem("Send the secret key or where to read it from, not both."));
        if (sp.GetService<SecretStore>() is not { Available: true } secrets) return (null, Problem("Secrets need the master key."));
        if (SecretReference.Parse(reference, out var bad) is not { } r) return (null, Problem(bad!));
        try { return (await secrets.ReadReferenceAsync(r, MayUseAppCredentials(http), ct), null); }
        catch (SecretReferenceException e) when (e.Code == "permission-denied") { return (null, new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult()); }
        catch (SecretReferenceException e) { return (null, Problem(e.Message)); }
    }

    /// <summary>Keeps a location's secret key as <paramref name="reference"/> (before its move starts). Null when kept.</summary>
    public static async Task<IResult?> KeepKeyReferenceAsync(IServiceProvider sp, string secretName, string? reference, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (KeepIn(reference) is { } source)   // saved with the location as a value: now moved into AWS
        {
            try { await sp.GetRequiredService<SecretStore>().MoveToSourceAsync(secretName, source, Who(http.User), MayUseAppCredentials(http), ct); return null; }
            catch (SecretReferenceException e) when (e.Code == "permission-denied") { return new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult(); }
            catch (SecretReferenceException e) { return Problem(e.Message); }
        }
        return await SetAsync(sp.GetRequiredService<SecretStore>(), secretName, null, reference, http, ct);
    }

    private static Dictionary<string, string> Locations(IEnumerable<Winche.Storage.Locations.ArchiveLocation?> list) =>
        list.OfType<Winche.Storage.Locations.ArchiveLocation>().Where(l => l.Kind != "code").DistinctBy(l => l.SecretName).ToDictionary(l => l.SecretName, l => l.Describe());

    /// <summary>A kept secret as the Secrets page lists it: its product, what it is, and the console page that changes it.</summary>
    private static object Describe(SecretInfo i, Dictionary<string, string> storage, Dictionary<string, string> assets)
    {
        var parts = i.Name.Split('.');
        (string Product, string Title, string? Link) what = i.Name switch
        {
            Winche.Auth.Email.SmtpEmailSender.SecretName => ("Authentication", "SMTP password (emails to users)", "/authentication/settings"),
            Winche.Integrity.WincheIntegrity.PlayServiceAccountSecret => ("Integrity", "Google service account (Play Integrity)", "/integrity"),
            _ when parts is ["integrity", "apps", var app, "turnstileSecret"] => ("Integrity", $"Turnstile secret key of the app {app} (the browser check)", "/integrity"),
            _ when parts is ["auth", "provider", var provider, "keycloakSync", "clientSecret"] => ("Authentication", $"Keycloak sync client secret of {provider}", "/authentication/methods"),
            _ when storage.TryGetValue(i.Name, out var where) => ("Storage", $"Secret key of {where}", "/storage/settings"),
            _ when assets.TryGetValue(i.Name, out var where) => ("Assets", $"Secret key of {where}", "/assets/settings"),
            _ when parts is ["storage", "location", _, "secretKey"] => ("Storage", "Secret key of a location no longer used", null),
            _ when parts is ["assets", "cdn", var cdn, "privateKey"] => ("Assets", $"Private key of the CDN {cdn} (signs its links)", "/assets/settings"),
            _ when parts is ["hooks", var hook, "signingSecret"] => ("Webhooks", $"Signing secret of the webhook {hook} (made by Winche)", null),
            _ => ("Other", i.Name, null),
        };
        return new { name = i.Name, product = what.Product, title = what.Title, link = what.Link, setAt = i.SetAt, setBy = i.SetBy, reference = ReferenceOf(i) };
    }

    internal static RouteGroupBuilder MapConsoleSecretsEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/secrets").RequireAuthorization(ConsoleRoles.AdminPolicy);

        // Every secret the project keeps, but functions' own and sources' keys (those have their own lists): which
        // product it belongs to, what it is, where it's changed, and whether it's a value or read from elsewhere.
        group.MapGet("", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var secrets = sp.GetService<SecretStore>();
            var all = secrets is { Available: true } ? await secrets.ListAsync("", ct) : [];
            var storage = Locations(sp.GetService<SettingsArea<Winche.Storage.Locations.StorageLocationSettings>>()?.Current is { } st
                ? [st.Active, st.Move?.To, st.Move?.From] : []);
            var assets = Locations(sp.GetService<SettingsArea<Winche.Assets.AssetLocationSettings>>()?.Current is { } at
                ? [at.Active, at.Move?.To] : []);
            return Results.Json(new
            {
                available = secrets?.Available ?? false,
                secrets = all.Where(i => !i.Name.StartsWith("functions.secret.", StringComparison.Ordinal) && !i.Name.StartsWith(SecretStore.SourcePrefix, StringComparison.Ordinal))
                    .Select(i => Describe(i, storage, assets)),
            });
        });

        group.MapGet("/sources", async (HttpContext http, IServiceProvider sp, bool? fresh, CancellationToken ct) =>
        {
            var secrets = sp.GetService<SecretStore>();
            // What the key each source would use may do there (found without changing anything; remembered a few minutes).
            var access = new Dictionary<string, SecretSourceAccess>();
            foreach (var s in secrets?.Sources ?? []) access[s.Name] = await secrets!.AccessAsync(s.Name, MayUseAppCredentials(http), fresh == true, ct);
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
                sources = (secrets?.Sources ?? []).OrderBy(s => s.Name).Select(s => new
                {
                    name = s.Name, title = s.Title, hint = s.LocatorHint, key = s.Key?.SecretName,
                    access = access[s.Name] is var a ? new { read = a.Read, write = a.Write, delete = a.Delete, missing = a.Missing, problem = a.Problem } : null,
                }),
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
