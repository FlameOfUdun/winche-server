using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Console.Apps;

/// <summary>
/// What this server's apps need to know (<see cref="WincheAppSetup"/>): its project, the products started here and
/// where they answer, and the sign-in providers' public client ids.
/// </summary>
public static class AppSetup
{
    /// <summary>The setup of the server <paramref name="services"/> belongs to, reached at <paramref name="baseUrl"/>.</summary>
    public static WincheAppSetup For(IServiceProvider services, string baseUrl, string projectId, string? name)
    {
        var isService = services.GetService<IServiceProviderIsService>();
        bool Has(Type t) => isService?.IsService(t) == true;
        // With project setup, only the products that have been started.
        var project = services.GetService<Project.ProjectSetup>()?.Current;
        bool Started(string product) => project is null || !project.SetupEnabled || project.IsStarted(product);
        var list = new List<WincheAppSetupService>();
        if (Has(typeof(Winche.Database.Runtime.DocumentDatabase)) && Started("database")) list.Add(new("database", "/documents/v1", "/documents/v1/ws"));
        if (Has(typeof(Winche.Storage.Services.FileStorage)) && Started("storage")) list.Add(new("storage", "/files/v1"));
        if (Has(typeof(Winche.Auth.IWincheAuth)) && Started("authentication")) list.Add(new("auth", "/auth/v1"));
        if (Has(typeof(Winche.Functions.WincheFunctions)) && Started("functions")) list.Add(new("functions", "/functions/v1"));
        if (Has(typeof(Winche.Assets.WincheAssets)) && Started("assets")) list.Add(new("assets", "/assets/v1"));
        var providers = services.GetService<Winche.Auth.WincheAuthOptions>()?.IdentityProviders
            .Select(p => new WincheAppSetupProvider(p.Name, p.Kind, p.DisplayName ?? p.Name, [.. p.Audiences]))
            .ToList() ?? [];
        return new WincheAppSetup(projectId, name, baseUrl.TrimEnd('/'), list, providers);
    }

    /// <summary>The address the request came in on, behind a proxy too (with forwarded headers applied).</summary>
    public static string BaseUrlOf(HttpRequest request) => WincheAppSetup.BaseUrlOf(request);
}

/// <summary>Maps <c>GET /.well-known/winche</c>.</summary>
public static class AppSetupEndpoints
{
    /// <summary>
    /// Publishes <see cref="AppSetup"/> at <c>GET /.well-known/winche</c>, for <c>winche configure</c> and anyone setting
    /// up an app: the project, which services this server runs and where, and the sign-in providers' public client ids.
    /// The project comes from the console's options (<c>ProjectId</c>, <c>ProjectName</c>).
    /// </summary>
    public static IEndpointConventionBuilder MapWincheAppSetup(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/.well-known/winche", (HttpContext http) =>
        {
            var project = http.RequestServices.GetService<Project.ProjectSetup>()?.Current;
            var setup = AppSetup.For(http.RequestServices, AppSetup.BaseUrlOf(http.Request), project?.Id ?? "winche", project?.Name);
            // ?app=<id>: the setup for that registered app (its id goes in winche_options.dart). Unknown ids: 404.
            if (http.Request.Query["app"].FirstOrDefault() is { Length: > 0 } id)
            {
                if (http.RequestServices.GetService<RegisteredApps>()?.Find(id) is not { } app)
                    return Results.Json(new { error = $"No app {id} is registered in project {setup.ProjectId}." }, statusCode: 404);
                setup = setup with { App = new WincheAppSetupApp(app.Id, app.Name) };
            }
            return Results.Json(setup);
        }).AllowAnonymous();
}
