using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Console.Identity;
using Winche.Console.Tabs;
using Winche.Functions;

namespace Winche.Console.Api;

/// <summary>
/// The Functions page and <c>winche functions deploy</c>: what's deployed, versions and rollback, running a callable.
/// The code lives in the app's repository; the CLI deploys it here (as the signed-in admin, or a CI token with the
/// <c>functions</c> scope), with a note. Deploying needs two-step sign-in where settings do, since it runs with the
/// server's rights.
/// </summary>
public static class ConsoleFunctionsEndpoints
{
    public sealed record DeployRequest(Dictionary<string, string>? Files, string? Note, Dictionary<string, ResourceEntry>? Resources = null);
    public sealed record ResourcesRequest(List<string>? Hashes);
    public sealed record NoteRequest(string? Note);
    public sealed record RunRequest(string? Name, JsonElement? Data, string? AsUid);
    public sealed record SecretRequest(string? Value);

    internal static bool Capability(IServiceProvider sp, ConsoleRole? role) => role == ConsoleRole.Admin && sp.GetService<WincheFunctions>() is not null;

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult Problem(string message, IReadOnlyList<string>? problems = null) =>
        ConsoleErrors.Problem("functions", message, problems);

    private static object Info(FunctionInfo f, WincheFunctions? functions = null) => new
    {
        name = f.Name, kind = f.Trigger.Kind, events = f.Trigger.Events, path = f.Trigger.Path, cron = f.Trigger.Cron, timeZone = f.Trigger.TimeZone,
        description = f.Trigger.Describe(), timeoutSeconds = functions?.TimeoutFor(f) ?? f.TimeoutSeconds, codeTimeoutSeconds = f.CodeTimeoutSeconds,
        secrets = f.Secrets ?? [],
        about = f.About, input = f.Input?.DeepClone(), output = f.Output?.DeepClone(), example = f.ExampleInput,
    };

    private static object Version(Deployment d, WincheFunctions? functions = null) => new
    {
        version = d.Version, note = d.Note, by = d.By, at = d.At, rolledBackFrom = d.RolledBackFrom, functions = d.Functions.Select(f => Info(f, functions)),
    };

    public static IEndpointRouteBuilder MapConsoleFunctionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/functions").RequireAuthorization(ConsoleRoles.AdminPolicy);
        var ci = new ConsoleCiScope(ConsoleCiScope.Functions);   // what a CI token may call

        group.MapGet("", async (WincheFunctions functions, FunctionsOptions options, IServiceProvider sp, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            string? runtimeProblem = null;
            try { DenoRuntime.Locate(options); }
            catch (FunctionsUnavailableException ex) { runtimeProblem = ex.Message; }
            var current = functions.Current;
            return Results.Json(new
            {
                current = current is null ? null : Version(current, functions),   // times as they apply now
                maxTimeoutSeconds = (int)options.MaxTimeout.TotalSeconds,
                files = current?.Files.Keys.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>(),   // names only: the code is in the repository
                resources = current?.ResourceFiles.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new { path = a.Key, size = a.Value.Size }) ?? [],
                runtimeProblem,
            });
        }).WithMetadata(ci);

        group.MapGet("/versions", async (FunctionStore store, CancellationToken ct) =>
            Results.Json(new { versions = (await store.ListAsync(50, ct)).Select(v => Version(v)) })).WithMetadata(ci);

        group.MapPost("/deploy", async (DeployRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Note)) return Problem("Say what changed: it goes in the version list.");
            try { return Results.Json(Version(await functions.DeployAsync(body.Files ?? [], body.Note.Trim(), Who(http.User), ct, resources: body.Resources))); }
            catch (FunctionDeployException ex) { return Problem(ex.Message, ex.Problems); }
            catch (FunctionsUnavailableException ex) { return Problem(ex.Message); }
        }).WithMetadata(ci);

        // Resources (the functions folder's resources/): the CLI asks which contents are new, sends only those, then deploys
        // naming them by hash. Each content is kept once, whatever the versions that use it.
        group.MapPost("/resources/missing", async (ResourcesRequest body, WincheFunctions functions, CancellationToken ct) =>
        {
            var hashes = body.Hashes ?? [];
            if (hashes.Count > WincheFunctions.MaxResources) return Problem($"There are more than {WincheFunctions.MaxResources} resources.");
            return Results.Json(new { missing = await functions.MissingResourcesAsync(hashes, ct) });
        }).WithMetadata(ci);

        group.MapPut("/resources/{hash}", async (string hash, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (http.Request.ContentLength > WincheFunctions.MaxResourceBytes) return Problem($"An resource is at most {WincheFunctions.MaxResourceBytes / 1024 / 1024} MB.");
            using var content = new MemoryStream();
            await http.Request.Body.CopyToAsync(content, ct);
            try { await functions.PutResourceAsync(content.ToArray(), hash, ct); }
            catch (FunctionDeployException ex) { return Problem(ex.Message); }
            return Results.NoContent();
        }).WithMetadata(ci).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(WincheFunctions.MaxResourceBytes + 1));

        group.MapPost("/versions/{version:int}/rollback", async (int version, NoteRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            try { return Results.Json(Version(await functions.RollBackAsync(version, string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(), Who(http.User), ct))); }
            catch (FunctionDeployException ex) { return Problem(ex.Message, ex.Problems); }
            catch (FunctionsUnavailableException ex) { return Problem(ex.Message); }
        }).WithMetadata(ci);

        // Secrets functions read with ctx.secrets.get: names, when and by whom they were set, which functions declare them.
        // Values go in and never come back out. Setting or removing one needs two-step sign-in where settings do. Admins
        // only: a CI token deploys, it doesn't handle secrets.
        group.MapGet("/secrets", async (WincheFunctions functions, FunctionSecrets secrets, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            return Results.Json(new
            {
                available = secrets.Available,
                secrets = (await secrets.ListAsync(functions.Current, ct)).Select(s => new { name = s.Name, isSet = s.IsSet, setAt = s.SetAt, setBy = s.SetBy, usedBy = s.UsedBy }),
            });
        });

        group.MapPut("/secrets/{name}", async (string name, SecretRequest body, HttpContext http, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            if (string.IsNullOrEmpty(body.Value)) return Problem("Give the secret's value.");
            if (!secrets.Available) return Problem("Secrets need the server's master key.");
            await secrets.SetAsync(name, body.Value, Who(http.User), ct);
            return Results.NoContent();
        });

        group.MapDelete("/secrets/{name}", async (string name, HttpContext http, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            return await secrets.RemoveAsync(name, ct) ? Results.NoContent() : new WincheError(WincheErrorCode.NotFound, $"There's no secret {name}.").ToResult();
        });

        // Runs a callable now, as a signed-out caller or as a given user id, showing its result and what it logged.
        group.MapPost("/run", async (RunRequest body, WincheFunctions functions, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) return Problem("Choose a function.");
            var data = body.Data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } d ? JsonNode.Parse(d.GetRawText()) : null;
            var caller = string.IsNullOrWhiteSpace(body.AsUid) ? FunctionCaller.Server
                : new FunctionCaller(body.AsUid.Trim(), new Dictionary<string, object?> { ["uid"] = body.AsUid.Trim() });
            var r = await functions.InvokeAsync(body.Name, data, caller, ct, kind: "callable");
            return Results.Json(new
            {
                ok = r.Ok, result = r.Result, error = r.Ok ? null : new { code = r.ErrorCode, message = r.ErrorMessage }, durationMs = r.DurationMs,
                logs = r.Logs.Select(l => new { level = l.Level, message = l.Message, at = l.At }),
            });
        });

        return app;
    }
}
