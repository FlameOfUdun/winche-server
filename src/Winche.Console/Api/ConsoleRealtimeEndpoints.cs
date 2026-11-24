using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;
using Winche.Realtime;

namespace Winche.Console.Api;

/// <summary>
/// The Realtime page: its lists (open and closed), a list's items, who's present at a path, closing and deleting. Rules
/// are the rules editor's (<c>rules/realtime</c>); lifecycle, archiving and limits are the <c>realtime</c> settings area.
/// </summary>
public static class ConsoleRealtimeEndpoints
{
    public sealed record PathRequest(string? Path);

    internal static bool Capability(ConsoleOptions options, ConsoleRole? role) => options.RealtimeTab is { } t && role is { } r && r >= t.MinRole;

    private static IResult Refused(RealtimeException e) => e.ToError().ToResult();

    public static IEndpointRouteBuilder MapConsoleRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ConsoleOptions>();
        var group = app.MapGroup("/realtime").RequireAuthorization(ConsoleRolePolicy.For(options.RealtimeTab!.MinRole));
        // Nothing of Realtime is read before it's started and set up.
        group.AddEndpointFilter(async (ctx, next) =>
            Project.ProjectSetup.IsReady(ctx.HttpContext.RequestServices, Project.WincheProducts.Realtime) ? await next(ctx)
                : ConsoleErrors.Problem("realtime", "Start Realtime first (Project → Products).", null));

        group.MapGet("/lists", async (string? prefix, string? state, string? after, int? limit, RealtimeStore store, CancellationToken ct) =>
        {
            if (state is not (null or "open" or "closed")) return ConsoleErrors.Problem("realtime", "state is open or closed.", null);
            var lists = await store.ListsAsync(prefix, state, after, Math.Clamp(limit ?? 100, 1, 500), ct);
            return Results.Json(new { lists = lists.Select(List), next = lists.Count == Math.Clamp(limit ?? 100, 1, 500) ? lists[^1].Path : null });
        });

        group.MapGet("/lists/items", async (string? path, long? after, int? limit, RealtimeStore store, CancellationToken ct) =>
        {
            try
            {
                var page = await store.ReadAsync(path ?? "", after ?? 0, Math.Clamp(limit ?? 100, 1, 500), ct);
                if (page.List is null) return Results.NotFound();
                return Results.Json(new { list = List(page.List), items = page.Items.Select(Item) });
            }
            catch (RealtimeException e) { return Refused(e); }
        });

        group.MapGet("/presence", async (string? path, RealtimePresence presence, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return ConsoleErrors.Problem("realtime", "Give a path.", null);
            return Results.Json(new { members = (await presence.MembersAsync(path, ct)).Select(m => new { uid = m.Uid, data = m.Data, joinedAt = m.JoinedAt }) });
        });

        group.MapPost("/lists/close", async (PathRequest body, RealtimeLists lists, CancellationToken ct) =>
        {
            try { return Results.Json(new { list = List(await lists.ServerCloseAsync(body.Path ?? "", ct)) }); }
            catch (RealtimeException e) { return Refused(e); }
        }).RequireAuthorization(ConsoleRoles.AdminPolicy);

        group.MapDelete("/lists", async (string? path, RealtimeStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.DeleteClosedAsync(path ?? "", ct) ? Results.NoContent()
                    : ConsoleErrors.Problem("realtime", "Only a closed list is deleted: close it first.", null);
            }
            catch (RealtimeException e) { return Refused(e); }
        }).RequireAuthorization(ConsoleRoles.AdminPolicy);

        return app;
    }

    private static object List(RealtimeListInfo l) => new { path = l.Path, state = l.State, count = l.Count, bytes = l.Bytes, openedAt = l.OpenedAt, closedAt = l.ClosedAt };

    /// <summary>An item as the console shows it: its payload as text when it's UTF-8 (JSON, usually), else its size only.</summary>
    private static object Item(RealtimeItem i)
    {
        string? text = null;
        try { text = new UTF8Encoding(false, true).GetString(i.Payload); } catch (DecoderFallbackException) { }
        if (text is { Length: > 4000 }) text = text[..4000] + "…";
        return new { key = i.Key, position = i.Position, at = i.At, by = i.By, size = i.Payload.Length, text };
    }
}
