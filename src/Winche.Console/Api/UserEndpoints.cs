using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Identity;

namespace Winche.Console.Api;

/// <summary>Admin-only account management under <c>/accounts</c>.</summary>
public static class UserEndpoints
{
    public sealed record CreateUserRequest(string Email, string? FirstName, string? LastName, string Role, string? Password);
    public sealed record UpdateUserRequest(string? FirstName, string? LastName, string? Email, string? Role, bool? Active, bool? TwoFactorRequired);
    public sealed record ResetPasswordRequest(string? NewPassword);

    /// <summary>
    /// On a server (its own console) an account is a server admin or not: what someone does in a project is that
    /// project's members list. Null when <paramref name="role"/> is fine here.
    /// </summary>
    internal static IResult? ServerRoleRefused(Options.ConsoleOptions options, string role) =>
        options.ServerConsole && role != ConsoleRoles.Admin
            ? Results.BadRequest(new { error = "On a server an account is a server admin or not: give people roles in a project (its members)." })
            : null;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts").RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapGet("/", async (ConsoleAccounts accounts, CancellationToken ct) => Results.Json((await accounts.ListAsync(ct)).Select(u => new
        {
            id = u.Id, email = u.Email, firstName = u.FirstName, lastName = u.LastName, role = u.Role, active = u.Active,
            twoFactorEnabled = u.TwoFactorEnabled, twoFactorRequired = u.TwoFactorRequired, lockedOut = u.LockedOut,
        })));

        group.MapPost("/", async (CreateUserRequest body, ConsoleAccounts accounts, Options.ConsoleOptions options, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email)) return Results.BadRequest(new { error = "Email is required." });
            // On a server, "none": an account that isn't a server admin (projects add it to their members).
            var role = options.ServerConsole && body.Role == "none" ? null : body.Role;
            if (role is not null && !ConsoleRoles.All.Contains(role)) return Results.BadRequest(new { error = "Invalid role." });
            if (role is not null && ServerRoleRefused(options, role) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Password))
                return Results.BadRequest(new { error = "A password is required (use an invite to onboard without one)." });
            try
            {
                var user = await accounts.CreateAsync(body.Email, body.Password, body.FirstName, body.LastName, role, ct: ct);
                return Results.Json(new { id = user.Id, email = user.Email });
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPut("/{id}", async (string id, UpdateUserRequest body, ConsoleAccounts accounts, Options.ConsoleOptions options, CancellationToken ct) =>
        {
            if (body.Role is not null && body.Role != "none" && ServerRoleRefused(options, body.Role) is { } refused) return refused;
            var user = await accounts.FindAsync(id, ct);
            if (user is null) return Results.NotFound();
            // A new email: set at once (an admin's change), and the account's sign-ins end.
            if (!string.IsNullOrWhiteSpace(body.Email) && !string.Equals(body.Email.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
            {
                try { await accounts.UseAsync(a => a.SetEmailAsync(id, body.Email.Trim(), ct)); }
                catch (Winche.Auth.AuthException ex) when (ex.Code == Winche.Auth.AuthErrorCodes.EmailExists)
                { return Results.Conflict(new { error = "Another account has that email." }); }
                catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
            }

            // Last-admin guards: demotion or deactivation of the only admin is rejected.
            var demoting = body.Role is not null && body.Role != ConsoleRoles.Admin && user.IsAdmin;
            var deactivating = body.Active == false && user.Active;
            if ((demoting || deactivating) && user.IsAdmin && await accounts.AdminCountAsync(ct) <= 1)
                return Results.BadRequest(new { error = "Cannot demote or deactivate the last admin." });

            // "none" takes the account's role away (on a server: no longer a server admin; project memberships stay).
            var clear = body.Role == "none";
            var role = body.Role is not null && ConsoleRoles.All.Contains(body.Role) ? body.Role : null;
            await accounts.UpdateAsync(id, body.FirstName, body.LastName, role, body.TwoFactorRequired, clearRole: clear, ct: ct);
            if (body.Active is { } active && active != user.Active) await accounts.UseAsync(a => a.SetDisabledAsync(id, !active, ct));
            Identity.ConsoleSession.Forget(id);   // their open sessions see it at their next request
            return Results.Ok();
        });

        group.MapPost("/{id}/reset-password", async (string id, ResetPasswordRequest body, ConsoleAccounts accounts, IServiceProvider sp, CancellationToken ct) =>
        {
            var user = await accounts.FindAsync(id, ct);
            if (user?.Email is not { } email) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.NewPassword))
            {
                // Email mode: send the user a reset link (requires the email adapter).
                var sender = ConsoleEmail.Sender(sp);
                if (sender is null) return Results.BadRequest(new { error = "Provide a new password (email is not configured)." });
                var link = await accounts.UseAsync(a => a.CreateEmailLinkAsync(Winche.Auth.Email.AuthEmailKinds.ResetPassword, email, ct));
                await sender.SendPasswordResetAsync(new ConsoleEmailRecipient(email, user.FirstName, user.LastName), link, ct);
                return Results.Ok();
            }
            try
            {
                await accounts.UseAsync(a => a.SetPasswordAsync(id, body.NewPassword, ct));
                return Results.Ok();
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/{id}/unlock", async (string id, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            if (await accounts.FindAsync(id, ct) is null) return Results.NotFound();
            await accounts.UseAsync(a => a.UnlockAsync(id, ct));
            return Results.Ok();
        });

        group.MapDelete("/{id}", async (string id, ConsoleAccounts accounts, HttpContext http, CancellationToken ct) =>
        {
            var user = await accounts.FindAsync(id, ct);
            if (user is null) return Results.NotFound();
            if (string.Equals(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, id, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "You cannot delete your own account." });
            if (user.IsAdmin && await accounts.AdminCountAsync(ct) <= 1)
                return Results.BadRequest(new { error = "Cannot delete the last admin." });
            await accounts.UseAsync(a => a.DeleteUserAsync(id, ct));
            Identity.ConsoleSession.Forget(id);
            return Results.NoContent();
        });

        return app;
    }
}
