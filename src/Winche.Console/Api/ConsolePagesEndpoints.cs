using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;
using Winche.Functions;

namespace Winche.Console.Api;

/// <summary>
/// The project's own console pages (<c>pages(...)</c> in its functions): listed in the sidebar for the roles that may see
/// them, and served from the live deployment in a sandboxed frame, where @winche-technologies/winche-admin acts as the person who opened it.
/// </summary>
/// <remarks>
/// A page is opened through a short-lived signed address (<c>pages/{name}/view/{token}/</c>): the frame has no cookies
/// of the console's and runs in an opaque origin (the sandbox CSP), so it can't call the console's API or read its
/// session. A page brings its own <c>@winche-technologies/winche-admin</c> (bundled, or a file in its folder); the SDK finds the channel
/// the console's bridge script opens, and its calls go over it to the console, which sends them to the admin API
/// (<c>admin</c>) with the person's own session, so their role limits them.
/// </remarks>
public static class ConsolePagesEndpoints
{

    private const string Purpose = "Winche.Console.Page.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>The policy the page frames get: scripts and styles from the page itself (and inline), no network of their own.</summary>
    internal const string Policy = "sandbox allow-scripts allow-forms allow-popups allow-modals allow-downloads; default-src 'self' data: blob:; "
        + "script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; "
        + "connect-src 'none'; frame-ancestors 'self'; form-action 'none'; base-uri 'none'";

    internal static string RoleName(ConsoleRole role) => role switch { ConsoleRole.Admin => "admin", ConsoleRole.Member => "member", _ => "viewer" };

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static ITimeLimitedDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public static IEndpointRouteBuilder MapConsolePagesEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/pages").RequireAuthorization(ConsoleRoles.ViewerPolicy);

        api.MapGet("", async (HttpContext http, WincheFunctions functions, ConsoleOptions options, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            var role = RoleName(ConsoleRolePolicy.Highest(http.User, options));
            return Results.Json(new
            {
                pages = ConsolePages.For(functions.Current, role).Select(f => new { name = f.Name, title = f.Page!.Title, icon = f.Page.Icon, order = f.Page.Order }),
            });
        });

        // A signed address for the page's frame: its name and who opened it, for 8 hours.
        api.MapPost("/{name}/open", async (string name, HttpContext http, WincheFunctions functions, ConsoleOptions options, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            var role = RoleName(ConsoleRolePolicy.Highest(http.User, options));
            if (!ConsolePages.For(functions.Current, role).Any(f => f.Name == name))
                return new WincheError(WincheErrorCode.NotFound, $"There's no console page {name} for your role.").ToResult();
            var token = Protector(http).Protect($"{name}\n{Who(http.User)}", Lifetime);
            var api = http.RequestServices.GetRequiredService<ConsolePrefix>().Api;
            return Results.Json(new
            {
                url = $"{http.Request.PathBase}{api}/pages/{Uri.EscapeDataString(name)}/view/{token}/",
                user = new { email = Who(http.User), role },
            });
        });

        // The page's files: no session (the frame has none), the signed address instead.
        app.MapGet("/pages/{name}/view/{token}/{**path}", async (string name, string token, string? path, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            string payload;
            try { payload = Protector(http).Unprotect(token); }
            catch (CryptographicException) { return Results.Text("This page link has ended: open the page again from the console.", statusCode: StatusCodes.Status403Forbidden); }
            if (payload.Split('\n')[0] != name) return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = Policy;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers.CacheControl = "private, no-cache";
            // The frame is sandboxed (its origin is "null"): its module scripts load in CORS mode, so its files say
            // anyone may read them (whoever has the signed address may anyway).
            http.Response.Headers.AccessControlAllowOrigin = "*";
            if (path == BridgePath) return Results.Text(Bridge, "text/javascript; charset=utf-8");
            await functions.RefreshAsync(ct);
            var page = functions.Current?.Functions.FirstOrDefault(f => f.Name == name && f.Trigger.Kind == "consolePage");
            if (page is null || ConsolePages.File(functions.Current!, page, path ?? "") is not { } file) return Results.NotFound();
            if (file.ContentType.StartsWith("text/html", StringComparison.Ordinal))
                return Results.Bytes(Encoding.UTF8.GetBytes(WithBridge(Encoding.UTF8.GetString(file.Content), Depth(path))), file.ContentType);
            return Results.Bytes(file.Content, file.ContentType);
        }).AllowAnonymous();

        return app;
    }

    internal const string BridgePath = "_winche/console.js";

    private static int Depth(string? path) => (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Length - ((path ?? "").EndsWith('/') || path is null or "" ? 0 : 1);

    /// <summary>The page's HTML with the bridge script first in its head (or at the top, without one).</summary>
    internal static string WithBridge(string html, int depth)
    {
        var tag = $"<script src=\"{string.Concat(Enumerable.Repeat("../", Math.Max(0, depth)))}{BridgePath}\"></script>";
        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        var end = head < 0 ? -1 : html.IndexOf('>', head);
        return end < 0 ? tag + html : html.Insert(end + 1, tag);
    }

    /// <summary>
    /// @winche-technologies/winche-admin's way out of the page: a channel to the console opened once (a MessageChannel), so only this page,
    /// not one it navigates to, can use it. Each call is answered with its result or its error (code and message).
    /// </summary>
    internal const string Bridge = """
        (() => {
          const key = Symbol.for("winche.page");
          if (globalThis[key]) return;
          let port = null, next = 1, connected;
          const pending = new Map();
          const ready = new Promise((r) => (connected = r));
          globalThis[key] = (op, args, protocol) => ready.then(() => new Promise((resolve, reject) => {
            const id = next++;
            pending.set(id, { resolve, reject });
            port.postMessage({ op: "admin", id, call: op, args, protocol });
          }));
          window.addEventListener("message", (e) => {
            if (port || e.source !== window.parent || e.data?.winche !== 1 || e.data.op !== "connect" || !e.ports[0]) return;
            port = e.ports[0];
            port.onmessage = (m) => {
              const d = m.data;
              if (d.op === "init") { connected(); return; }
              const p = pending.get(d.id);
              if (!p) return;
              pending.delete(d.id);
              if (d.error) p.reject(Object.assign(new Error(d.error.message), { name: "WincheError", code: d.error.code }));
              else p.resolve(d.result);
            };
          });
          window.parent.postMessage({ winche: 1, op: "hello" }, "*");
        })();
        """;
}
