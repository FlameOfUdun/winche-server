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
    /// <param name="Plugins">The plugins winche.json lists, when it lists them: the deployment has exactly these. Null keeps the ones installed.</param>
    public sealed record DeployRequest(Dictionary<string, string>? Files, string? Note, Dictionary<string, ResourceEntry>? Resources = null, List<PluginRequest>? Plugins = null);
    public sealed record ResourcesRequest(List<string>? Hashes);
    public sealed record NoteRequest(string? Note);
    public sealed record RunRequest(string? Name, JsonElement? Data, string? AsUid);
    /// <summary>A secret from a form: a value, a reference, or a value with the reference <c>keep:aws-sm</c> (kept in AWS).</summary>
    public sealed record SecretRequest(string? Value, string? Reference = null);
    public sealed record MoveSecretRequest(string? Store);

    internal static bool Capability(IServiceProvider sp, ConsoleRole? role) => role == ConsoleRole.Admin && sp.GetService<WincheFunctions>() is not null;

    private static string? Who(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? user.Identity?.Name;

    private static IResult Problem(string message, IReadOnlyList<string>? problems = null) =>
        ConsoleErrors.Problem("functions", message, problems);

    private static object Info(FunctionInfo f, WincheFunctions? functions = null) => new
    {
        name = f.Name, kind = f.Trigger.Kind, events = f.Trigger.Events, path = f.Trigger.Path, cron = f.Trigger.Cron, timeZone = f.Trigger.TimeZone,
        description = f.Describe(), timeoutSeconds = functions?.TimeoutFor(f) ?? f.TimeoutSeconds, codeTimeoutSeconds = f.CodeTimeoutSeconds,
        secrets = f.Secrets ?? [], plugin = f.Plugin, integrity = f.RequireIntegrity,
        about = f.About, input = f.Input?.DeepClone(), output = f.Output?.DeepClone(), example = f.ExampleInput,
        // Its instances: what's in force (and from where: setting, code, default), and what its code says.
        limits = functions is null || f.Trigger.Kind == "before" ? null : Limits(functions.LimitsFor(f)),
        codeLimits = new { memoryMb = f.Limits.MemoryMb, minInstances = f.Limits.MinInstances, maxInstances = f.Limits.MaxInstances,
            reservedInstances = f.Limits.ReservedInstances, isolate = f.Limits.Isolate },
    };

    private static object Limits(FunctionLimitsInForce l) => new
    {
        memoryMb = l.MemoryMb, minInstances = l.MinInstances, maxInstances = l.MaxInstances, reservedInstances = l.ReservedInstances,
        isolate = l.Isolate, from = l.From,
    };

    private static object Version(Deployment d, WincheFunctions? functions = null) => new
    {
        version = d.Version, note = d.Note, by = d.By, at = d.At, rolledBackFrom = d.RolledBackFrom, functions = d.Functions.Select(f => Info(f, functions)),
        plugins = d.PluginList.Select(p => new { id = p.Id, package = p.Package, version = p.Version, source = p.Source }),
    };

    public static IEndpointRouteBuilder MapConsoleFunctionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/functions").RequireAuthorization(ConsoleRoles.AdminPolicy);
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
                maxMemoryMb = options.MaxMemoryMb,
                maxConcurrency = options.MaxConcurrency,
                files = current?.Files.Keys.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>(),   // names only: the code is in the repository
                resources = current?.ResourceFiles.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new { path = a.Key, size = a.Value.Size }) ?? [],
                runtimeProblem,
            });
        }).WithMetadata(ci);

        // A function's instances on this server now, its process, and what happened to them lately.
        group.MapGet("/{name}/instances", async (string name, WincheFunctions functions, DenoRuntime runtime, FunctionsOptions options, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            if (functions.Current?.Functions.FirstOrDefault(f => f.Name == name) is not { } fn)
                return Results.Json(new { error = $"There's no function {name}." }, statusCode: StatusCodes.Status404NotFound);
            var processes = await runtime.StatsAsync(ct);
            var own = processes.FirstOrDefault(p => p.Function == name);
            var process = own ?? processes.FirstOrDefault(p => p.Function is null);
            var limits = fn.Trigger.Kind == "before" ? null : functions.LimitsFor(fn);
            return Results.Json(new
            {
                server = functions.ServerName,
                // Other servers running the project: their instances aren't in this view (see their logs).
                otherServers = await functions.OtherServersAsync(ct),
                limits = limits is null ? null : Limits(limits),
                process = process is null ? null : new
                {
                    own = own is not null, running = process.Running, pid = process.Pid, heapMb = process.HeapMb, startedAt = process.StartedAt,
                    restarts = Math.Max(0, process.Starts - 1),
                },
                // Asked for its own process but runs in the shared one (the budget or the process count).
                refused = limits is { } l && (l.Isolate || l.MemoryMb > options.SharedProcessMemoryMb) && own is null && runtime.Refused(name),
                instances = processes.SelectMany(p => p.Instances).Where(i => i.Function == name)
                    .Select(i => new { state = i.State, heapMb = i.HeapMb, runs = i.Runs, ageSeconds = i.AgeSeconds, busySeconds = i.BusySeconds }),
                events = runtime.Events.Where(e => e.Function == name || (e.Process is null && e.Function is null && own is null)).Take(50)
                    .Select(e => new { at = e.At, kind = e.Kind, message = e.Message }),
                budget = new { totalMb = runtime.Budget.TotalMb, usedMb = runtime.Budget.UsedMb },
            });
        });

        group.MapGet("/versions", async (FunctionStore store, CancellationToken ct) =>
            Results.Json(new { versions = (await store.ListAsync(50, ct)).Select(v => Version(v)) })).WithMetadata(ci);

        group.MapPost("/deploy", async (DeployRequest body, HttpContext http, WincheFunctions functions, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Note)) return Problem("Say what changed: it goes in the version list.");
            try
            {
                var plugins = body.Plugins is null ? null : await functions.ResolveDeployPluginsAsync(body.Plugins, Who(http.User), ct);
                return Results.Json(Version(await functions.DeployAsync(body.Files ?? [], body.Note.Trim(), Who(http.User), ct, resources: body.Resources, plugins: plugins)));
            }
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

        // Secrets functions read (secrets.define, key.value()): names, when and by whom they were set, which functions list them.
        // Values go in and never come back out. Setting or removing one needs two-step sign-in where settings do. Admins
        // only: a CI token deploys, it doesn't handle secrets.
        group.MapGet("/secrets", async (WincheFunctions functions, FunctionSecrets secrets, CancellationToken ct) =>
        {
            await functions.RefreshAsync(ct);
            return Results.Json(new
            {
                available = secrets.Available,
                secrets = (await secrets.ListAsync(functions.Current, ct)).Select(s => new { name = s.Name, isSet = s.IsSet, setAt = s.SetAt, setBy = s.SetBy, usedBy = s.UsedBy, reference = s.Reference?.ToString(), managed = s.Managed }),
            });
        });

        group.MapPut("/secrets/{name}", async (string name, SecretRequest body, HttpContext http, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            if (string.IsNullOrEmpty(body.Value) && string.IsNullOrWhiteSpace(body.Reference)) return Problem("Give the secret's value, or where to read it from.");
            if (!secrets.Available) return Problem("Secrets need the server's master key.");
            var store = http.RequestServices.GetRequiredService<Winche.Secrets.SecretStore>();
            return await ConsoleSecretsEndpoints.SetAsync(store, FunctionSecrets.Prefix + name, body.Value, body.Reference, http, ct) ?? Results.NoContent();
        });

        // Reads it now (a reference through its source): whether it works, never the value.
        group.MapPost("/secrets/{name}/test", async (string name, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            var result = await secrets.TestAsync(name, ct);
            return Results.Json(new { ok = result.Ok, problem = result.Problem, code = result.Code });
        });

        group.MapDelete("/secrets/{name}", async (string name, HttpContext http, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            // One Winche keeps in AWS is deleted there too (recoverable for a week in Secrets Manager); AWS refusing stops it.
            var store = http.RequestServices.GetRequiredService<Winche.Secrets.SecretStore>();
            try { return await store.DeleteAsync(FunctionSecrets.Prefix + name, keepInSource: false, ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct) ? Results.NoContent() : new WincheError(WincheErrorCode.NotFound, $"There's no secret {name}.").ToResult(); }
            catch (Winche.Secrets.SecretReferenceException e) when (e.Code == "permission-denied") { return new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult(); }
            catch (Winche.Secrets.SecretReferenceException e) { return Problem(e.Message); }
        });

        // Moves a value kept here into AWS (Secrets Manager or Parameter Store): only the reference stays here.
        group.MapPost("/secrets/{name}/move", async (string name, MoveSecretRequest body, HttpContext http, FunctionSecrets secrets, CancellationToken ct) =>
        {
            if (await ConsoleAuthSettingsEndpoints.RequireTwoFactorAsync(http) is { } refused) return refused;
            if (FunctionSecrets.Problem(name) is { } bad) return Problem(bad);
            if (string.IsNullOrWhiteSpace(body.Store)) return Problem("Say where to keep it: aws-sm or aws-ssm.");
            var store = http.RequestServices.GetRequiredService<Winche.Secrets.SecretStore>();
            try { await store.MoveToSourceAsync(FunctionSecrets.Prefix + name, body.Store.Trim(), Who(http.User), ConsoleSecretsEndpoints.MayUseAppCredentials(http), ct); return Results.NoContent(); }
            catch (Winche.Secrets.SecretReferenceException e) when (e.Code == "permission-denied") { return new WincheError(WincheErrorCode.PermissionDenied, e.Message, "secrets").ToResult(); }
            catch (Winche.Secrets.SecretReferenceException e) { return Problem(e.Message); }
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

        group.MapConsolePluginsEndpoints(ci);
        return app;
    }
}
