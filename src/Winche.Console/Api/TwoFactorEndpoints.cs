using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Console.Identity;

namespace Winche.Console.Api;

/// <summary>Self-service TOTP two-factor enrollment under <c>/api/auth/2fa</c>.</summary>
public static class TwoFactorEndpoints
{
    public sealed record CodeRequest(string Code);

    private const string Issuer = "Winche Console";

    public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/2fa").RequireAuthorization(ConsoleRoles.ViewerPolicy);

        // Begin enrollment: a new authenticator key and the otpauth URI for a QR.
        group.MapPost("/setup", async (ConsoleAccounts accounts, HttpContext http) =>
        {
            if (await accounts.CurrentAsync(http.User, http.RequestAborted) is not { } user) return Results.Unauthorized();
            try
            {
                var enrollment = await accounts.UseAsync(a => a.StartTotpEnrollmentAsync(user.Id, http.RequestAborted));
                return Results.Json(new { sharedKey = FormatKey(enrollment.Secret), authenticatorUri = enrollment.OtpAuthUri });
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Confirm enrollment: verify a code, turn two-step on, return one-time recovery codes.
        group.MapPost("/enable", async (CodeRequest body, ConsoleAccounts accounts, HttpContext http) =>
        {
            if (await accounts.CurrentAsync(http.User, http.RequestAborted) is not { } user) return Results.Unauthorized();
            try
            {
                var codes = await accounts.UseAsync(a => a.ConfirmTotpEnrollmentAsync(user.Id, Normalize(body.Code),
                    Guid.TryParse(http.User.FindFirst(ConsoleSession.SessionClaim)?.Value, out var keep) ? keep : null, http.RequestAborted));
                return Results.Json(new { recoveryCodes = codes });
            }
            catch (Winche.Auth.AuthException) { return Results.BadRequest(new { error = "Invalid code." }); }
        });

        group.MapPost("/disable", async (ConsoleAccounts accounts, HttpContext http) =>
        {
            if (await accounts.CurrentAsync(http.User, http.RequestAborted) is not { } user) return Results.Unauthorized();
            if (user.TwoFactorRequired)
                return Results.Json(new { error = "Two-factor is required by an administrator." }, statusCode: StatusCodes.Status403Forbidden);
            await accounts.UseAsync(a => a.ResetMfaAsync(user.Id, http.RequestAborted));
            return Results.Ok();
        });

        return app;
    }

    private static string Normalize(string? code) => (code ?? "").Replace(" ", "").Replace("-", "");

    private static string FormatKey(string key)
    {
        var chunks = new List<string>();
        for (var i = 0; i < key.Length; i += 4) chunks.Add(key.Substring(i, Math.Min(4, key.Length - i)));
        return string.Join(" ", chunks).ToLowerInvariant();
    }
}
