using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;

namespace Winche.Console.Api;

public static class AuthConfigEndpoints
{
    /// <summary>Anonymous bootstrap: which project this console is (for the sign-in page).</summary>
    public static IEndpointRouteBuilder MapAuthConfigEndpoint(this IEndpointRouteBuilder app, ConsoleOptions options)
    {
        // The project's name, so the sign-in page can say which console this is.
        object Project() => app.ServiceProvider.GetService<Project.ProjectSetup>()?.Current is { } p
            ? new { id = p.Id, name = p.Name }
            : new { id = options.ProjectId, name = options.ProjectName ?? options.ProjectId };
        app.MapGet("/session/config", () => Results.Json(new { provider = "identity", project = Project() }));
        return app;
    }
}
