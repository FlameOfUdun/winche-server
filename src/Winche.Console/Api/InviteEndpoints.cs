using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Identity;

namespace Winche.Console.Api;

/// <summary>Invite management (Admin) under <c>/invites</c> + anonymous acceptance under
/// <c>/invites/accept</c>. The user account is created only on accept.</summary>
public static class InviteEndpoints
{
    public sealed record CreateInviteRequest(string Email, string Role, string? FirstName, string? LastName,
        bool RequireName, bool RequireTwoFactor, int ExpiresInHours);
    public sealed record AcceptInviteRequest(string Token, string Password, string? FirstName, string? LastName);

    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/invites").RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapGet("/", async (ConsoleStore db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var list = await db.OpenInvitesAsync();
            return Results.Json(list.Select(i => Dto(i, now)));
        });

        group.MapPost("/", async (CreateInviteRequest body, ConsoleStore db,
            ConsoleAccounts accounts, IServiceProvider sp, ConsoleInviteTokens tokens,
            ConsolePrefix prefix, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email)) return Results.BadRequest(new { error = "Email is required." });
            if (!ConsoleRoles.All.Contains(body.Role)) return Results.BadRequest(new { error = "Invalid role." });
            if (UserEndpoints.ServerRoleRefused(sp.GetRequiredService<Options.ConsoleOptions>(), body.Role) is { } refused) return refused;
            if (body.ExpiresInHours is < 1 or > 720) return Results.BadRequest(new { error = "Expiry must be 1–720 hours." });

            // Without console email the invite is still made: its link is shared by hand.
            var sender = ConsoleEmail.Sender(sp);

            var email = body.Email.Trim().ToLowerInvariant();
            if (await accounts.FindByEmailAsync(email, http.RequestAborted) is not null)
                return Results.Conflict(new { error = "A user with that email already exists." });

            var now = DateTimeOffset.UtcNow;
            if (await db.HasPendingInviteAsync(email, now, http.RequestAborted))
                return Results.Conflict(new { error = "A pending invite already exists for that email." });

            var invite = new ConsoleInvite
            {
                Id = Guid.NewGuid(), Email = email, Role = body.Role,
                FirstName = body.FirstName, LastName = body.LastName,
                RequireName = body.RequireName, RequireTwoFactor = body.RequireTwoFactor,
                CreatedAt = now, ExpiresAt = now.AddHours(body.ExpiresInHours),
                CreatedByUserId = Project.WincheProjectsCaller.Of(http.User).UserId,
            };
            await db.SaveInviteAsync(invite, http.RequestAborted);

            var link = MintLink(tokens, invite, TimeSpan.FromHours(body.ExpiresInHours), http, prefix.Value);
            if (sender is not null)
                await sender.SendInviteAsync(new ConsoleEmailRecipient(invite.Email, invite.FirstName, invite.LastName), link, http.RequestAborted);
            return Results.Json(Dto(invite, now, link, emailed: sender is not null));
        });

        group.MapGet("/{id:guid}/link", async (Guid id, ConsoleStore db, ConsoleInviteTokens tokens,
            ConsolePrefix prefix, HttpContext http) =>
        {
            var invite = await db.FindInviteAsync(id);
            var now = DateTimeOffset.UtcNow;
            if (invite is null || invite.Status(now) != "pending") return Results.StatusCode(StatusCodes.Status410Gone);
            var link = MintLink(tokens, invite, invite.ExpiresAt - now, http, prefix.Value);
            return Results.Json(new { link });
        });

        group.MapPost("/{id:guid}/resend", async (Guid id, ConsoleStore db, IServiceProvider sp,
            ConsoleInviteTokens tokens, ConsolePrefix prefix, HttpContext http) =>
        {
            var invite = await db.FindInviteAsync(id);
            if (invite is null) return Results.NotFound();
            if (invite.AcceptedAt is not null) return Results.Conflict(new { error = "Invite already accepted." });
            var sender = ConsoleEmail.Sender(sp);
            if (sender is null) return Results.BadRequest(new { error = ConsoleEmail.Missing(sp, "Invites") });

            var now = DateTimeOffset.UtcNow;
            var lifetime = invite.ExpiresAt - invite.CreatedAt;
            if (lifetime <= TimeSpan.Zero) lifetime = TimeSpan.FromHours(72);
            invite.CreatedAt = now;
            invite.ExpiresAt = now.Add(lifetime);
            invite.RevokedAt = null;
            await db.SaveInviteAsync(invite, http.RequestAborted);

            var link = MintLink(tokens, invite, lifetime, http, prefix.Value);
            await sender.SendInviteAsync(new ConsoleEmailRecipient(invite.Email, invite.FirstName, invite.LastName), link, http.RequestAborted);
            return Results.Json(new { link });
        });

        group.MapDelete("/{id:guid}", async (Guid id, ConsoleStore db) =>
        {
            var invite = await db.FindInviteAsync(id);
            if (invite is null) return Results.NotFound();
            if (invite.AcceptedAt is not null) return Results.Conflict(new { error = "Invite already accepted." });
            if (invite.RevokedAt is null) { invite.RevokedAt = DateTimeOffset.UtcNow; await db.SaveInviteAsync(invite); }
            return Results.NoContent();
        });

        var accept = app.MapGroup("/invites/accept");

        accept.MapGet("/", async (string? token, ConsoleStore db, ConsoleInviteTokens tokens, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            if (!tokens.TryUnprotect(token, out var id)) return Results.StatusCode(StatusCodes.Status410Gone);
            var invite = await db.FindInviteAsync(id);
            var now = DateTimeOffset.UtcNow;
            if (invite is null || invite.Status(now) != "pending") return Results.StatusCode(StatusCodes.Status410Gone);
            // Who sent it: their name, else their email.
            var by = invite.CreatedByUserId is { } inviter ? await accounts.FindAsync(inviter, ct) : null;
            var name = by is null ? "" : string.Join(' ', new[] { by.FirstName, by.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
            var invitedBy = by is null ? null : name.Length > 0 ? name : by.Email;
            return Results.Json(new
            {
                email = invite.Email, firstName = invite.FirstName, lastName = invite.LastName,
                requireName = invite.RequireName, requireTwoFactor = invite.RequireTwoFactor,
                role = invite.Role, invitedBy, expiresAt = invite.ExpiresAt,
            });
        });

        accept.MapPost("/", async (AcceptInviteRequest body, ConsoleStore db, ConsoleAccounts accounts,
            ConsoleInviteTokens tokens, CancellationToken ct) =>
        {
            if (!tokens.TryUnprotect(body.Token, out var id)) return Results.StatusCode(StatusCodes.Status410Gone);
            var invite = await db.FindInviteAsync(id);
            var now = DateTimeOffset.UtcNow;
            if (invite is null || invite.Status(now) != "pending") return Results.StatusCode(StatusCodes.Status410Gone);

            var firstName = string.IsNullOrWhiteSpace(body.FirstName) ? invite.FirstName : body.FirstName.Trim();
            var lastName = string.IsNullOrWhiteSpace(body.LastName) ? invite.LastName : body.LastName.Trim();
            if (invite.RequireName && (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName)))
                return Results.BadRequest(new { error = "First and last name are required." });

            if (await accounts.FindByEmailAsync(invite.Email, ct) is not null)
                return Results.Conflict(new { error = "A user with that email already exists." });
            try { await accounts.CreateAsync(invite.Email, body.Password ?? "", firstName, lastName, invite.Role, invite.RequireTwoFactor, ct); }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }

            invite.AcceptedAt = now;
            await db.SaveInviteAsync(invite, ct);
            return Results.Ok();
        });

        return app;
    }

    private static string MintLink(ConsoleInviteTokens tokens, ConsoleInvite invite, TimeSpan lifetime,
        HttpContext http, string prefix) =>
        ConsoleLinks.InviteLink(http, prefix, tokens.Protect(invite.Id, lifetime));

    private static object Dto(ConsoleInvite i, DateTimeOffset now, string? link = null, bool emailed = false) => new
    {
        id = i.Id, email = i.Email, role = i.Role, firstName = i.FirstName, lastName = i.LastName,
        requireName = i.RequireName, requireTwoFactor = i.RequireTwoFactor,
        createdAt = i.CreatedAt, expiresAt = i.ExpiresAt, status = i.Status(now), link, emailed,
    };
}
