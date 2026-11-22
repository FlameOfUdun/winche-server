using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Console.Api;
using Winche.Console.Identity;
using Winche.Core;
using Winche.Secrets;

namespace Winche.Console.Project;

/// <summary>
/// The server's own settings (Winche Server provides them): a settings area kept with the server, the deployment's values
/// it starts from, and the server's secrets (passwords). The console shows and saves them for server admins.
/// </summary>
public interface IWincheServerSettings
{
    SettingsArea Area { get; }
    SettingsStore Store { get; }
    SecretStore Secrets { get; }

    /// <summary>The deployment's values (<c>Winche__…</c>): where the settings start, shown beside what's in force.</summary>
    JsonNode DeploymentJson { get; }

    /// <summary>The secrets the page may set (name → what it is), e.g. a database password.</summary>
    IReadOnlyDictionary<string, string> SecretNames { get; }

    /// <summary>The identity providers people can sign in to the console with (each may be turned off).</summary>
    IReadOnlyList<Identity.ConsoleOidcSettings> ConsoleProviders { get; }

    /// <summary>Passwords only for server admins: a provider is on, and the server says so.</summary>
    bool ConsolePasswordsOnlyForAdmins { get; }

    /// <summary>A provider's client secret for the console, if it has one.</summary>
    Task<string?> ConsoleProviderSecretAsync(string id, CancellationToken ct);

    /// <summary>The one address the providers send people back to (register it there).</summary>
    string ConsoleSignInCallbackUrl { get; }

    /// <summary>Where providers tell the console someone signed out (back-channel logout; register it there).</summary>
    string ConsoleSignInLogoutUrl => ConsoleSignInCallbackUrl[..ConsoleSignInCallbackUrl.LastIndexOf('/')] + "/backchannel-logout";

    /// <summary>Where a sign-in goes when it doesn't say (the server's console).</summary>
    string ConsoleSignInReturnPath { get; }

    /// <summary>A secret was set or removed (some are copied where they're used, e.g. a Keycloak sync's to the console accounts).</summary>
    Task SecretChangedAsync(string name, CancellationToken ct) => Task.CompletedTask;

}

public static class ServerSettingsEndpoints
{
    public sealed record SaveRequest(JsonElement? Settings, string? Reason, int? BasedOn = null);
    public sealed record SecretRequest(string? Value, string? Reference = null);
    public sealed record TestEmailRequest(string? To);
    public sealed record SignInTestRequest(Identity.ConsoleOidcSettings? Settings, string? ClientSecret);

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult? Refuse(HttpContext http, Options.ConsoleOptions options) =>
        options.ServerSettings is null ? Results.NotFound()
        : !WincheProjectsCaller.Of(http.User).ServerAdmin
            ? Results.Json(new { error = "Only server admins see the server's settings." }, statusCode: StatusCodes.Status403Forbidden)
            : null;

    internal static IEndpointRouteBuilder MapConsoleServerSettingsEndpoints(this IEndpointRouteBuilder app, Options.ConsoleOptions options)
    {
        var group = app.MapGroup("/server").RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapGet("/settings", async (HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http, options) is { } refused) return refused;
            var s = options.ServerSettings!;
            var secrets = new List<object>();
            foreach (var (name, about) in s.SecretNames)
            {
                var info = await s.Secrets.InfoAsync(name, ct);
                secrets.Add(new { name, about, setAt = info?.SetAt, setBy = info?.SetBy, reference = Api.ConsoleSecretsEndpoints.ReferenceOf(info) });
            }
            return Results.Json(new
            {
                settings = s.Area.CurrentJson,
                deployment = s.DeploymentJson,
                source = s.Area.Source == SettingsSource.Saved ? "console" : "deployment",
                version = s.Area.Version is { } v ? new { number = v.Number, at = v.At, by = v.By, reason = v.Reason } : null,
                secrets,
                consoleSignIn = new { callbackUrl = s.ConsoleSignInCallbackUrl, logoutUrl = s.ConsoleSignInLogoutUrl },
                functionsMemory = Winche.Functions.FunctionsMemoryBudget.FromContainer() is var b
                    ? new { totalMb = b.TotalMb, usedMb = b.UsedMb, source = b.Source } : null,
            }, SettingsArea.Json);
        });

        group.MapPut("/settings", async (SaveRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http, options) is { } refused) return refused;
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } twoFactor) return twoFactor;
            if (body.Settings is not { ValueKind: JsonValueKind.Object } doc) return ConsoleErrors.Problem("invalid-settings", "Send the settings to save.", null);
            if (string.IsNullOrWhiteSpace(body.Reason)) return ConsoleErrors.Problem("invalid-settings", "Say why you're making this change: it goes in the history.", null);
            try
            {
                var node = JsonNode.Parse(doc.GetRawText())!;
                // A provider taken off the list takes its client and sync secrets with it, first (AWS may refuse: then
                // nothing changes).
                var after = (node["consoleProviders"] as JsonArray)?.Select(p => p?["id"]?.GetValue<string>()).OfType<string>().ToHashSet() ?? [];
                foreach (var gone in options.ServerSettings!.ConsoleProviders.Select(p => p.Id).Where(id => !after.Contains(id)))
                {
                    await options.ServerSettings.Secrets.DeleteAsync(ConsoleOidc.SecretFor(gone), keepInSource: false, ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct);
                    await options.ServerSettings.Secrets.DeleteAsync(ConsoleOidcSettings.KeycloakSyncSecretFor(gone), keepInSource: false, ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct);
                }
                var v = await options.ServerSettings.Store.SaveAsync(options.ServerSettings.Area.Name, node, Who(http.User), body.Reason, body.BasedOn, ct);
                return Results.Json(new { number = v.Number, at = v.At, by = v.By, reason = v.Reason });
            }
            catch (SettingsValidationException ex) { return ConsoleErrors.Problem("invalid-settings", "These settings can't be saved.", ex.Errors); }
        });

        group.MapGet("/settings/history", async (HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http, options) is { } refused) return refused;
            var versions = await options.ServerSettings!.Store.HistoryAsync(options.ServerSettings.Area.Name, 50, ct);
            return Results.Json(new
            {
                versions = versions.Select(v => new { id = v.Id, number = v.Number, at = v.At, by = v.By, reason = v.Reason, current = v.Number == options.ServerSettings.Area.Version?.Number }),
            });
        });

        // A password (write-only): set it, or empty to remove it.
        group.MapPut("/secrets/{name}", async (string name, SecretRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http, options) is { } refused) return refused;
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } twoFactor) return twoFactor;
            var s = options.ServerSettings!;
            if (!s.SecretNames.ContainsKey(name)) return Results.NotFound();
            if (string.IsNullOrEmpty(body.Value) && string.IsNullOrWhiteSpace(body.Reference)) await s.Secrets.DeleteAsync(name, ct);
            else if (await Api.ConsoleSecretsEndpoints.SetAsync(s.Secrets, name, body.Value, body.Reference, http, ct) is { } bad) return bad;
            await s.SecretChangedAsync(name, ct);
            return Results.NoContent();
        });
        // Tries unsaved sign-in settings: the page opens the address returned, which goes to the provider and reports back.
        group.MapPost("/console-sign-in/test", async (SignInTestRequest body, HttpContext http, CancellationToken ct) =>
        {
            if (Refuse(http, options) is { } refused) return refused;
            if (body.Settings is not { } s) return ConsoleErrors.Problem("invalid-settings", "Send the sign-in settings to try.", null);
            if ((s with { Enabled = true }).Problems() is { Count: > 0 } problems) return ConsoleErrors.Problem("invalid-settings", "These settings can't be tried yet.", problems);
            // No secret typed: the provider's saved one (a provider being edited).
            var secret = string.IsNullOrEmpty(body.ClientSecret) && s.Id is { Length: > 0 }
                ? await options.ServerSettings!.ConsoleProviderSecretAsync(s.Id, ct) : body.ClientSecret;
            return Results.Json(new
            {
                url = "v1/session/oidc/start?test=" + Uri.EscapeDataString(Identity.ConsoleOidc.ProtectTest(http.RequestServices, s, secret)),
                callbackUrl = options.ServerSettings!.ConsoleSignInCallbackUrl,
            });
        });

        return app;
    }
}
