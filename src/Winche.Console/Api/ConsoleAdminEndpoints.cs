using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;
using Winche.Core;
using Winche.Database.Runtime.History;
using Winche.Functions;

namespace Winche.Console.Api;

/// <summary>
/// <c>admin</c>: what @winche-technologies/winche-admin sends from outside functions (a console page, another server with a token), run by
/// the same bindings functions use, limited by the caller's role in the project: a viewer reads, a member also writes
/// the data (documents, files, the app's users), an admin may also call functions. A page may not call functions
/// (callables are the apps').
/// </summary>
public static class ConsoleAdminEndpoints
{
    public sealed record AdminRequest(string? Op, JsonElement Args);

    /// <summary>Set by the console's web app on the calls it relays for a page.</summary>
    public const string PageHeader = "X-Winche-Page";

    /// <summary>The protocol the calling @winche-technologies/winche-admin speaks (refused unless this server's).</summary>
    public const string ProtocolHeader = "X-Winche-Protocol";

    /// <summary>The least role <paramref name="op"/> needs.</summary>
    public static ConsoleRole RoleFor(string op) =>
        AdminOps.IsRead(op) ? ConsoleRole.Viewer : AdminOps.IsDataWrite(op) ? ConsoleRole.Member : ConsoleRole.Admin;

    public static IEndpointRouteBuilder MapConsoleAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin", async (AdminRequest body, HttpContext http, ConsoleOptions options, CancellationToken ct) =>
        {
            if (http.Request.Headers[ProtocolHeader].FirstOrDefault() is { Length: > 0 } spoken && spoken != FunctionsSdk.Protocol.ToString())
                return new WincheError(WincheErrorCode.FailedPrecondition,
                    $"This @winche-technologies/winche-admin speaks protocol {spoken}; this server speaks {FunctionsSdk.Protocol} (@winche-technologies/winche-admin {FunctionsSdk.Major}.x).").ToResult();
            var op = body.Op?.Trim() ?? "";
            if (op.Length == 0 || !op.Contains('.'))
                return new WincheError(WincheErrorCode.InvalidArgument, "Give an operation, e.g. db.get.").ToResult();
            var args = body.Args.ValueKind == JsonValueKind.Object ? JsonNode.Parse(body.Args.GetRawText())!.AsObject() : new JsonObject();
            if (http.Request.Headers.ContainsKey(PageHeader) && op.StartsWith("functions.", StringComparison.Ordinal))
                return new WincheError(WincheErrorCode.PermissionDenied, "A console page can't call functions: callables are for the app's users.").ToResult();
            var role = ConsoleRolePolicy.Highest(http.User, options);
            var needs = RoleFor(op);
            if (role < needs)
                return new WincheError(WincheErrorCode.PermissionDenied,
                    $"{op} needs the {ConsolePagesEndpoints.RoleName(needs)} role in this project (yours: {ConsolePagesEndpoints.RoleName(role)}).").ToResult();
            if (needs == ConsoleRole.Admin && await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            // Writes are the caller's in the document history, as the console's own edits are.
            using var _ = DocumentWriteActor.Use(ConsoleDataEndpoints.ConsoleActor(http.User));
            try
            {
                return Results.Json(new { result = await AdminOps.RunAsync(http.RequestServices, op, args, ct) });
            }
            catch (FunctionCallException ex)
            {
                return new WincheError(Code(ex.Code), ex.Message).ToResult();
            }
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);
        return app;
    }

    private static WincheErrorCode Code(string code) => code switch
    {
        "not-found" => WincheErrorCode.NotFound,
        "already-exists" => WincheErrorCode.AlreadyExists,
        "permission-denied" => WincheErrorCode.PermissionDenied,
        "failed-precondition" => WincheErrorCode.FailedPrecondition,
        "aborted" => WincheErrorCode.Aborted,
        "unavailable" => WincheErrorCode.Unavailable,
        "deadline-exceeded" => WincheErrorCode.DeadlineExceeded,
        "resource-exhausted" => WincheErrorCode.ResourceExhausted,
        "unauthenticated" => WincheErrorCode.Unauthenticated,
        "internal" => WincheErrorCode.Internal,
        _ => WincheErrorCode.InvalidArgument,
    };
}
