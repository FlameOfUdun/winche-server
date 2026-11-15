using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth.AspNetCore;
using Winche.Core;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Users;

namespace Winche.Console.Api;

/// <summary>
/// <c>api/app-auth/settings</c> — the Authentication tab's Settings: trusted identity providers and their token mapping
/// (Winche.Auth's settings area <c>auth.identityProviders</c>), a token test, and the change history. Admins only.
/// Changing them needs <c>AddWincheTokens(t =&gt; t.UseSettings())</c> and <c>AddWincheConfig</c>; otherwise read-only.
/// </summary>
public static class ConsoleAuthSettingsEndpoints
{
    public sealed record SaveProvidersRequest(JsonElement? Settings, string? Reason, int? BasedOn = null);
    public sealed record CheckProviderRequest(string? Issuer, string? Authority, bool? RequireHttps);
    public sealed record TestTokenRequest(string? Token, JsonElement? Settings);
    public sealed record ReasonRequest(string? Reason);
    /// <param name="SyncSecrets">Keycloak sync client secrets typed in, by provider name (empty: removed).</param>
    public sealed record SaveSignInRequest(JsonElement? Settings, string? SmtpPassword, string? Reason, Dictionary<string, string>? SyncSecrets = null, int? BasedOn = null);
    public sealed record TestEmailRequest(JsonElement? Settings, string? SmtpPassword, string? To);

    /// <summary>Null when the console admin may change sensitive settings; otherwise the refusal.</summary>
    internal static async Task<IResult?> RequireTwoFactorAsync(HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<ConsoleOptions>();
        if (!options.RequireTwoFactorForSettings) return null;
        if (http.RequestServices.GetService<ConsoleAccounts>() is { } accounts && await accounts.CurrentAsync(http.User, http.RequestAborted) is { TwoFactorEnabled: true })
            return null;
        if (Identity.ConsoleOidc.ProviderTwoStep(http, options)) return null;   // the identity provider did it
        return Results.Json(new
        {
            error = new
            {
                code = "TWO_FACTOR_REQUIRED",
                message = "Turn on two-step sign-in for your console account (your account menu) before changing these settings.",
                errors = Array.Empty<string>(),
            },
        }, statusCode: 403);
    }

    private static string? Who(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static object Version(SettingsVersion v, SettingsStore? store) => new
    {
        id = v.Id, area = v.Area, areaTitle = store?.Area(v.Area)?.Title ?? v.Area, number = v.Number, at = v.At, by = v.By, reason = v.Reason,
        restoredFrom = v.RestoredFrom, reset = v.Document is null,
    };

    private static IResult Problem(string message, IReadOnlyList<string>? errors = null) =>
        ConsoleErrors.Problem("invalid-settings", message, errors);

    public static IEndpointRouteBuilder MapConsoleAuthSettingsEndpoints(this IEndpointRouteBuilder app, bool tokens = true, string root = "/api/app-auth")
    {
        var group = app.MapGroup(root + "/settings").RequireAuthorization(ConsoleRoles.AdminPolicy);

        // Winche Auth's own settings (o.UseSettings()): methods, providers, two-step, sessions, lockout, email.
        group.MapGet("/signin", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var area = sp.GetService<SettingsArea<Winche.Auth.Settings.WincheAuthSettings>>();
            if (area is null)
                return Results.Json(new
                {
                    available = false,
                    whyReadOnly = sp.GetService<Winche.Auth.WincheAuthOptions>() is null
                        ? "Winche Auth isn't installed in this app."
                        : "The app's code sets these: add o.UseSettings() to AddWincheAuth(…) to change them here.",
                });
            var store = sp.GetService<SettingsStore>();
            var secrets = sp.GetService<SecretStore>();
            var password = secrets is null ? null : await secrets.InfoAsync(Winche.Auth.Email.SmtpEmailSender.SecretName, ct);
            var isService = sp.GetService<IServiceProviderIsService>();
            // Which providers' Keycloak sync has its client secret (never the secret).
            var syncSecrets = new Dictionary<string, object?>();
            if (secrets is { Available: true })
                foreach (var p in area.Current.Providers.Where(p => p.KeycloakSync is not null))
                    syncSecrets[p.Name] = await secrets.InfoAsync(Winche.Auth.Settings.KeycloakSyncSettings.SecretName(p.Name), ct) is { } i
                        ? new { setAt = i.SetAt, setBy = i.SetBy } : null;
            return Results.Json(new
            {
                available = true,
                syncSecrets,
                settings = area.Current,
                codeDefault = area.CodeDefault,
                source = area.Source == SettingsSource.Saved ? "console" : "code",
                version = area.Version is { } v ? Version(v, store) : null,
                editable = store is not null,
                whyReadOnly = store is null ? "The app needs AddWincheConfig(…) to save settings." : null,
                smtpPassword = password is null ? null : new { setAt = password.SetAt, setBy = password.SetBy },
                secretsAvailable = secrets?.Available ?? false,
                appSender = isService?.IsService(typeof(Winche.Auth.Email.IAuthEmailSender)) == true,
            }, SettingsArea.Json);
        });

        group.MapPut("/signin", async (SaveSignInRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SettingsArea<Winche.Auth.Settings.WincheAuthSettings>>() is not { } area)
                return Problem("The app's code sets these: add o.UseSettings() to AddWincheAuth(…).");
            if (sp.GetService<SettingsStore>() is not { } store) return Problem("The app needs AddWincheConfig(…) to save settings.");
            if (body.Settings is not { ValueKind: JsonValueKind.Object } doc) return Problem("Send the settings to save.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're making this change: it goes in the history.");
            var node = JsonNode.Parse(doc.GetRawText())!;
            if (area.Validate(node) is { Count: > 0 } problems) return Problem("These settings can't be saved.", problems);
            if (!string.IsNullOrEmpty(body.SmtpPassword))
            {
                var secrets = sp.GetRequiredService<SecretStore>();
                if (!secrets.Available) return Problem("Secrets need the project's master key: set WincheConfigOptions.MasterKey.");
                await secrets.SetAsync(Winche.Auth.Email.SmtpEmailSender.SecretName, body.SmtpPassword, Who(http.User), ct);
            }
            if (body.SyncSecrets is { Count: > 0 })
            {
                var secrets = sp.GetRequiredService<SecretStore>();
                if (!secrets.Available) return Problem("Secrets need the project's master key: set WincheConfigOptions.MasterKey.");
                foreach (var (provider, value) in body.SyncSecrets)
                {
                    var name = Winche.Auth.Settings.KeycloakSyncSettings.SecretName(provider);
                    if (string.IsNullOrEmpty(value)) await secrets.DeleteAsync(name, ct);
                    else await secrets.SetAsync(name, value, Who(http.User), ct);
                }
            }
            try { return Results.Json(Version(await store.SaveAsync(Winche.Auth.Settings.WincheAuthSettings.Area, node, Who(http.User), body.Reason, body.BasedOn, ct), store)); }
            catch (SettingsValidationException ex) { return Problem("These settings can't be saved.", ex.Errors); }
        });

        // Send a test message through the draft's SMTP server, before saving.
        group.MapPost("/signin/testEmail", async (TestEmailRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            Winche.Auth.Settings.WincheAuthSettings? draft;
            try { draft = body.Settings is { ValueKind: JsonValueKind.Object } d ? d.Deserialize<Winche.Auth.Settings.WincheAuthSettings>(SettingsArea.Json) : null; }
            catch (JsonException ex) { return Problem($"The settings don't have the expected shape: {ex.Message}"); }
            if (draft?.Email.Smtp is not { } smtp) return Problem("Fill in the SMTP server.");
            var to = string.IsNullOrWhiteSpace(body.To) ? Who(http.User) : body.To.Trim();
            if (to is null || !to.Contains('@')) return Problem("Give an address to send the test to.");
            var password = string.IsNullOrEmpty(body.SmtpPassword)
                ? sp.GetService<SecretStore>() is { Available: true } secrets ? await secrets.GetAsync(Winche.Auth.Email.SmtpEmailSender.SecretName, ct) : null
                : body.SmtpPassword;
            try
            {
                await Winche.Auth.Email.SmtpEmailSender.SendTestAsync(new Winche.Auth.Email.SmtpOptions
                {
                    Host = smtp.Host, Port = smtp.Port, Security = smtp.Security, Username = string.IsNullOrWhiteSpace(smtp.Username) ? null : smtp.Username,
                    FromAddress = smtp.FromAddress, FromName = string.IsNullOrWhiteSpace(smtp.FromName) ? null : smtp.FromName,
                }, string.IsNullOrWhiteSpace(draft.Email.AppName) ? "Winche" : draft.Email.AppName, password, to, ct);
                return Results.Json(new { sentTo = to });
            }
            catch (Winche.Auth.AuthException ex) { return Problem(ex.Message); }
        });

        if (!tokens) return MapHistory(group);

        // The providers in force, where they come from, and whether the console may change them.
        group.MapGet("/providers", (WincheTokenProviders providers, IServiceProvider sp) =>
        {
            var store = sp.GetService<SettingsStore>();
            var area = providers.Area;
            return Results.Json(new
            {
                settings = providers.Current,
                codeDefault = area?.CodeDefault ?? providers.Current,
                source = area?.Source == SettingsSource.Saved ? "console" : "code",
                version = area?.Version is { } v ? Version(v, store) : null,
                editable = area is not null && store is not null,
                whyReadOnly = area is null ? "The app's code sets these. To change them here, the app adds AddWincheTokens(t => t.UseSettings())."
                    : store is null ? "The app needs AddWincheConfig(…) to save settings." : null,
            }, SettingsArea.Json);
        });

        group.MapPut("/providers", async (SaveProvidersRequest body, HttpContext http, WincheTokenProviders providers, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await RequireTwoFactorAsync(http) is { } refused) return refused;
            var store = sp.GetService<SettingsStore>();
            if (providers.Area is null || store is null) return Problem("These settings are set in the app's code and can't be changed here.");
            if (body.Settings is not { ValueKind: JsonValueKind.Object } doc) return Problem("Send the settings to save.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're making this change: it goes in the history.");
            try
            {
                var saved = await store.SaveAsync(WincheAuthDefaults.SettingsArea, JsonNode.Parse(doc.GetRawText())!, Who(http.User), body.Reason, body.BasedOn, ct);
                return Results.Json(Version(saved, store));
            }
            catch (SettingsValidationException ex) { return Problem("These settings can't be saved.", ex.Errors); }
            catch (InvalidOperationException ex) { return Problem(ex.Message); }
        });

        // Does this address belong to an OpenID Connect provider, and what does it say about itself?
        group.MapPost("/providers/check", async (CheckProviderRequest body, WincheTokenInspector inspector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Issuer)) return Problem("Give the provider's address.");
            var r = await inspector.CheckProviderAsync(body.Issuer.Trim(), body.Authority, body.RequireHttps ?? true, ct);
            return Results.Json(new { found = r.Found, issuer = r.Issuer, keys = r.Keys, algorithms = r.Algorithms, error = r.Error });
        });

        // What a pasted token would become, under the saved settings or a draft.
        group.MapPost("/providers/test", async (TestTokenRequest body, WincheTokenInspector inspector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token)) return Problem("Paste a token.");
            IdentityProvidersSettings? draft = null;
            if (body.Settings is { ValueKind: JsonValueKind.Object } s)
            {
                try { draft = s.Deserialize<IdentityProvidersSettings>(SettingsArea.Json); }
                catch (JsonException ex) { return Problem($"The draft settings don't have the expected shape: {ex.Message}"); }
            }
            var r = await inspector.InspectAsync(body.Token, draft, ct);
            return Results.Json(new
            {
                wouldSignIn = r.WouldSignIn, provider = r.Provider,
                checks = r.Checks.Select(c => new { outcome = c.Outcome, text = c.Text }),
                auth = r.Auth, notPassed = r.NotPassed,
            });
        });

        return MapHistory(group);
    }

    internal static IEndpointRouteBuilder MapHistory(RouteGroupBuilder group)
    {
        // Every saved change to the settings the console manages.
        group.MapGet("/history", async ([FromQuery] string? area, IServiceProvider sp, CancellationToken ct) =>
        {
            if (sp.GetService<SettingsStore>() is not { } store) return Results.Json(new { versions = Array.Empty<object>(), editable = false });
            var versions = await store.HistoryAsync(area, 200, ct);
            var current = store.Areas.Where(a => a.Version is not null).Select(a => a.Version!.Id).ToHashSet();
            return Results.Json(new
            {
                editable = true,
                versions = versions.Select(v => new
                {
                    id = v.Id, area = v.Area, areaTitle = store.Area(v.Area)?.Title ?? v.Area, number = v.Number, at = v.At, by = v.By,
                    reason = v.Reason, restoredFrom = v.RestoredFrom, reset = v.Document is null, current = current.Contains(v.Id),
                }),
            });
        });

        group.MapPost("/history/{id:long}/restore", async (long id, ReasonRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SettingsStore>() is not { } store) return Problem("Settings aren't saved in this app.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're restoring it: it goes in the history.");
            try { return Results.Json(Version(await store.RestoreAsync(id, Who(http.User), body.Reason, ct), store)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SettingsValidationException ex) { return Problem("That version no longer fits this app's settings.", ex.Errors); }
        });

        group.MapPost("/{area}/reset", async (string area, ReasonRequest body, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            if (await RequireTwoFactorAsync(http) is { } refused) return refused;
            if (sp.GetService<SettingsStore>() is not { } store) return Problem("Settings aren't saved in this app.");
            if (string.IsNullOrWhiteSpace(body.Reason)) return Problem("Say why you're resetting: it goes in the history.");
            try { return Results.Json(Version(await store.ResetAsync(area, Who(http.User), body.Reason, ct), store)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        return group;
    }
}
