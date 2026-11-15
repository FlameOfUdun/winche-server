using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Console.Api;
using Winche.Console.Email;
using Winche.Console.Identity;

namespace Winche.Console.Project;

/// <summary>
/// Who may use this project's console, and as what, when accounts are the server's and projects are many
/// (<c>UseProjectMembers()</c>; Winche Server's projects other than the first). Kept in the project's config database
/// (<c>winche_console_members</c>). Server admins (accounts with the Admin role) are admins of every project.
/// </summary>
public sealed class ProjectMembers
{
    internal const string Table = "winche_console_members";
    private static readonly string[] Schema =
    [
        $"""
        CREATE TABLE IF NOT EXISTS {Table} (
            user_id  uuid        PRIMARY KEY,
            role     text        NOT NULL,
            added_at timestamptz NOT NULL,
            added_by text        NULL
        );
        """,
        // Console accounts are a Winche Auth's now: their ids are text (the members of before don't carry over).
        $"DELETE FROM {Table}; ALTER TABLE {Table} ALTER COLUMN user_id TYPE text;",
    ];

    private readonly NpgsqlDataSource _db;
    private readonly ConcurrentDictionary<string, (string? Role, DateTimeOffset At)> _cache = new();
    private bool _ready;

    public ProjectMembers(IServiceProvider services) =>
        _db = services.GetKeyedService<NpgsqlDataSource>("WincheConfigOptions")
            ?? throw new InvalidOperationException("Project members need AddWincheConfig(…).");

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ready) return;
        await Winche.Core.WincheSchema.EnsureAsync(_db, "console-members", Schema, ct);
        _ready = true;
    }

    /// <summary>The member's role here, or null (not a member). Cached for 30 seconds.</summary>
    public async Task<string?> RoleOfAsync(string userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromSeconds(30)) return hit.Role;
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"SELECT role FROM {Table} WHERE user_id = $1");
        cmd.Parameters.AddWithValue(userId);
        var role = await cmd.ExecuteScalarAsync(ct) as string;
        _cache[userId] = (role, DateTimeOffset.UtcNow);
        return role;
    }

    public async Task<IReadOnlyList<(string UserId, string Role, DateTimeOffset AddedAt)>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"SELECT user_id, role, added_at FROM {Table} ORDER BY added_at");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(string, string, DateTimeOffset)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    public async Task SetAsync(string userId, string role, string? by, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"""
            INSERT INTO {Table} (user_id, role, added_at, added_by) VALUES ($1, $2, now(), $3)
            ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(role);
        cmd.Parameters.AddWithValue((object?)by ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    public async Task RemoveAsync(string userId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var cmd = _db.CreateCommand($"DELETE FROM {Table} WHERE user_id = $1");
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.TryRemove(userId, out _);
    }
}

/// <summary>
/// Turns an account's server-wide roles into its role in this project: server admins stay Admin (and carry
/// <see cref="ServerAdminClaim"/>), everyone else gets their membership's role, or none.
/// </summary>
internal sealed class ProjectRoleClaims(ProjectMembers members) : IClaimsTransformation
{
    public const string ServerAdminClaim = "winche:server-admin";
    internal const string DoneClaim = "winche:project-role";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == DoneClaim)) return principal;
        if (principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId) return principal;
        var serverAdmin = principal.IsInRole(ConsoleRoles.Admin);
        var role = serverAdmin ? ConsoleRoles.Admin : await members.RoleOfAsync(userId);

        var identity = new ClaimsIdentity(principal.Identity!.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaims(principal.Claims.Where(c => c.Type != ClaimTypes.Role));
        // The account's own role stays readable (the first project, which goes by it, is listed from here too).
        foreach (var r in principal.FindAll(ClaimTypes.Role)) identity.AddClaim(new Claim(WincheProjectsCaller.AccountRoleClaim, r.Value));
        if (role is not null) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        if (serverAdmin) identity.AddClaim(new Claim(ServerAdminClaim, "true"));
        identity.AddClaim(new Claim(DoneClaim, role ?? ""));
        return new ClaimsPrincipal(identity);
    }
}

public static class ProjectMemberEndpoints
{
    public sealed record AddRequest(string? Email, string? Role);
    public sealed record RoleRequest(string? Role);

    private static readonly string[] Roles = [ConsoleRoles.Admin, ConsoleRoles.Member, ConsoleRoles.Viewer];
    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;
    private static IResult Problem(string message) => ConsoleErrors.Problem("members", message, null);

    internal static IEndpointRouteBuilder MapConsoleProjectMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/members").RequireAuthorization(ConsoleRoles.AdminPolicy);

        // The members, and the server admins (who are admins here without being listed).
        group.MapGet("", async (ProjectMembers members, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            var list = await members.ListAsync(ct);
            var all = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id);
            return Results.Json(new
            {
                members = list.Where(m => all.TryGetValue(m.UserId, out var a) && !a.IsAdmin).Select(m => new
                {
                    userId = m.UserId, email = all[m.UserId].Email, firstName = all[m.UserId].FirstName, lastName = all[m.UserId].LastName,
                    role = m.Role, addedAt = m.AddedAt, passwordSet = all[m.UserId].HasPassword,
                }),
                serverAdmins = all.Values.Where(a => a.IsAdmin).Select(a => new { userId = a.Id, email = a.Email, firstName = a.FirstName, lastName = a.LastName }),
            });
        });

        // Adds someone by email: an existing account gets access at once; a new one is made (with access to nothing
        // else) and gets a link to set its password — emailed when sending is set up, else returned to share.
        group.MapPost("", async (AddRequest body, HttpContext http, ProjectMembers members, ConsoleAccounts accounts, IServiceProvider sp, CancellationToken ct) =>
        {
            var email = body.Email?.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains('@')) return Problem("Give their email address.");
            if (!Roles.Contains(body.Role)) return Problem("Pick Admin, Member or Viewer.");
            var user = await accounts.FindByEmailAsync(email, ct);
            string? link = null;
            var emailed = false;
            if (user is null)
            {
                try { user = await accounts.CreateAsync(email, null, null, null, role: null, ct: ct); }
                catch (Winche.Auth.AuthException ex) { return Problem(ex.Message); }
                link = await accounts.UseAsync(a => a.CreateEmailLinkAsync(Winche.Auth.Email.AuthEmailKinds.ResetPassword, email, ct));
                // Sent as a reset: the link sets the new account's password, then they sign in here.
                if (ConsoleEmail.Sender(sp) is { } sender)
                {
                    await sender.SendPasswordResetAsync(new ConsoleEmailRecipient(email, null, null), link, ct);
                    emailed = true;
                }
            }
            else if (user.IsAdmin)
            {
                return Problem($"{email} is a server admin: an admin of every project already.");
            }
            await members.SetAsync(user.Id, body.Role!, Who(http.User), ct);
            return Results.Json(new { userId = user.Id, newAccount = link is not null, emailed, link = emailed ? null : link });
        });

        group.MapPut("/{userId}", async (string userId, RoleRequest body, HttpContext http, ProjectMembers members, CancellationToken ct) =>
        {
            if (!Roles.Contains(body.Role)) return Problem("Pick Admin, Member or Viewer.");
            if (await members.RoleOfAsync(userId, ct) is null) return Results.NotFound();
            await members.SetAsync(userId, body.Role!, Who(http.User), ct);
            return Results.NoContent();
        });

        group.MapDelete("/{userId}", async (string userId, ProjectMembers members, CancellationToken ct) =>
        {
            await members.RemoveAsync(userId, ct);
            return Results.NoContent();
        });
        return app;
    }
}
