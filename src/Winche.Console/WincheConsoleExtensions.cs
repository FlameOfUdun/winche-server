using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Console.Api;
using Winche.Console.Diagnostics;
using Winche.Console.Email;
using Winche.Console.Identity;
using Winche.Console.Logs;
using Winche.Console.Options;
using Winche.Console.Rules;
using Winche.Console.Spa;
using Winche.Console.Tabs;
using Winche.Database.Constants;
using Winche.Database.Runtime;
using Winche.Storage.Constants;
using Winche.Storage.Services;

namespace Winche.Console;

/// <summary>
/// Attaches the Winche admin console to a host app that has already registered Winche.Database and
/// Winche.Storage. The console brings its own authentication (EF Core + ASP.NET Core Identity): supply
/// a connection string for its auth database; it protects its own endpoints with Admin/Member/Viewer
/// roles. No host auth wiring is needed.
/// </summary>
public static class WincheConsoleExtensions
{
    public static IServiceCollection AddWincheConsole(this IServiceCollection services, Action<ConsoleOptions> configure)
    {
        var options = new ConsoleOptions();
        configure(options);

        services.AddSingleton(options);
        options.EmailSenderRegistration?.Invoke(services);
        services.AddSingleton<ConsolePrefix>();

        services.TryAddSingleton(TimeProvider.System);
        if (options.AuthenticationTab || options.ServerConsole)
        {
            // Which directory the Users tab manages (Winche Auth, Keycloak): a setting the console can change. The
            // server's console has the accounts' Users page (Server settings → Authentication).
            services.AddSingleton<Users.UserDirectories>();
        }
        if (options.DatabaseTab is not null)
            services.AddSingleton(sp => new Api.IndexSuggestions(sp.GetRequiredService<Winche.Database.Querying.CollectionIndexResolver>()));
        if (options.DatabaseRulesEditor is not null || options.StorageRulesEditor is not null || options.AssetsRulesEditor is not null)
        {
            // The versioned rules store reuses the console's connection string (a dedicated table, never
            // the identity tables). Mandatory whenever any rules editor is enabled — including Keycloak
            // mode, where ConnectionString is otherwise unused.
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new InvalidOperationException(
                    "Enabling the rules editor (UseDatabaseRulesEditor/UseStorageRulesEditor) requires ConsoleOptions.ConnectionString — the versioned rules store reuses it.");

            services.AddSingleton(new RuleStoreFactory(options.ConnectionString));
            services.AddHostedService<ConsoleRulesStartupService>();
            services.AddHostedService<ConsoleRulesSyncService>();

            if (options.DatabaseRulesEditor is not null)
            {
                services.AddSingleton(new RuleSubsystemRegistration(
                    RuleSubsystems.Database,
                    WincheDatabaseKeys.RuleEngine,
                    options.DatabaseRulesEditor.ApplyPersistedRulesOnStartup,
                    options.DatabaseRulesEditor.SyncInterval));
            }

            if (options.StorageRulesEditor is not null)
            {
                services.AddSingleton(new RuleSubsystemRegistration(
                    RuleSubsystems.Storage,
                    WincheStorageKeys.RULE_ENGINE_KEY,
                    options.StorageRulesEditor.ApplyPersistedRulesOnStartup,
                    options.StorageRulesEditor.SyncInterval));
            }

            if (options.AssetsRulesEditor is not null)
            {
                services.AddSingleton(new RuleSubsystemRegistration(
                    RuleSubsystems.Assets,
                    Winche.Assets.AssetsServiceCollectionExtensions.RulesKey,
                    options.AssetsRulesEditor.ApplyPersistedRulesOnStartup,
                    options.AssetsRulesEditor.SyncInterval));
            }
        }

        if (options.AnyLogs)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new InvalidOperationException(
                    "The Logs tab (AddLogsTab / UseLogs) requires ConsoleOptions.ConnectionString — the entries are kept in the console's database.");
            var prefixes = new List<string>();
            if (options.DatabaseTab?.Logs == true) prefixes.Add(ConsoleLogsEndpoints.DatabaseScope);
            if (options.StorageTab?.Logs == true) prefixes.Add(ConsoleLogsEndpoints.StorageScope);
            services.AddSingleton(new ConsoleLogQueue(options.Logs, options.AppLogsTab, prefixes));
            services.AddSingleton(_ => new ConsoleLogStore(options.LogsDatabase, options.Logs));   // made by DI, so disposed with the app
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, ConsoleLoggerProvider>();
            // The console's collector receives every level whatever the host's filters say (so a source can be set
            // to Debug from the console without changing what reaches the app's other log outputs); it then keeps
            // only what the levels set in code or in the console allow.
            services.Configure<Microsoft.Extensions.Logging.LoggerFilterOptions>(o =>
                Microsoft.Extensions.Logging.FilterLoggingBuilderExtensions.AddFilter<ConsoleLoggerProvider>(o, null, Microsoft.Extensions.Logging.LogLevel.Trace));
            services.AddHostedService<ConsoleLogWriter>();
        }

        // Console API tokens (winche login in Identity mode, and CI tokens), in either mode.
        services.AddSingleton<ConsoleTokenStore>();
        services.AddSingleton<Project.ProjectSetup>();
        services.AddSingleton<Project.ProductSetupRunner>();
        services.AddSingleton<Project.ProjectImageStore>();
        services.AddSingleton<Apps.RegisteredApps>();
        services.AddSingleton<Apps.AppUsage>();
        if (options.ProjectSetup)
        {
            Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, Project.ProjectSettings.Area, "Project", new Project.ProjectSettings(), s => s.Problems());
            Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, Apps.AppsSettings.Area, "Apps", new Apps.AppsSettings(), s => s.Problems());
            services.AddHostedService<Apps.AppUsageFlusher>();
        }
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ConsoleTokenHandler>(ConsoleTokenHandler.Scheme, null);

        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new InvalidOperationException(
                    "AddWincheConsole requires ConsoleOptions.ConnectionString (the console's own auth database).");
            services.AddConsoleIdentity(options);
            services.AddHostedService<ConsoleStartupService>();
            // After the console's own tables exist (it looks at them to carry an older project over).
            if (options.ProjectSetup) services.AddHostedService<Project.ProjectStartupService>();
            if (options.ProjectMembers)
            {
                services.AddSingleton<Project.ProjectMembers>();
                services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, Project.ProjectRoleClaims>();
            }
        }

        return services;
    }

    /// <summary>
    /// The console at <paramref name="prefix"/>: its pages there, its API at <c>{prefix}/v1</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheConsole(
        this IEndpointRouteBuilder endpoints, string prefix = "/console")
    {
        var root = endpoints.MapGroup(prefix);
        MapApi(endpoints, root.MapGroup("/v1"), pages: prefix, api: prefix + "/v1", project: false);
        ConsoleSpa.Map(root, prefix);
        return root;
    }

    /// <summary>
    /// The console's API only, at the root of these endpoints (the request's path base says where: Winche Server hands a
    /// project's console calls, <c>/console/v1/projects/&lt;id&gt;/…</c>, to that project's app this way). Links to the
    /// console's pages point at <paramref name="pages"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheConsoleApi(this IEndpointRouteBuilder endpoints, string pages = "/console")
    {
        var group = endpoints.MapGroup("");
        MapApi(endpoints, group, pages, api: "", project: true);
        return group;
    }

    /// <summary>
    /// The console's API as a pipeline branch of its own (routing, the accounts' services, authorization), for an app
    /// whose other routes are something else: Winche Server's project apps, whose console calls arrive with the path
    /// base <c>/console/v1/projects/&lt;id&gt;</c>. The console's endpoints sign in with the console's schemes (their
    /// policies name them), so the branch has no default authentication of its own.
    /// </summary>
    public static IApplicationBuilder UseWincheConsoleApi(this IApplicationBuilder branch, string pages = "/console")
    {
        branch.UseRouting();
        branch.Use(Identity.ConsoleAccountsEndpoint.UseAccountsServicesAsync);
        branch.UseAuthorization();
        branch.UseEndpoints(endpoints => endpoints.MapWincheConsoleApi(pages));
        return branch;
    }

    /// <param name="project">A project's calls (<c>/console/v1/projects/&lt;id&gt;/…</c>): its own areas only. Signing in,
    /// the accounts, invites, the projects and Server settings are the server's console's.</param>
    private static void MapApi(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, string pages, string api, bool project)
    {
        var links = endpoints.ServiceProvider.GetRequiredService<ConsolePrefix>();
        links.Value = pages;
        links.Api = api;
        var options = endpoints.ServiceProvider.GetRequiredService<ConsoleOptions>();

        // Surface the most common deployment mistake (behind a TLS-terminating proxy without
        // UseForwardedHeaders -> cookie/OIDC login 401s) as one actionable log line. Logs once.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            ForwardedHeadersDiagnostic.Inspect(ctx.HttpContext);
            return await next(ctx);
        });
        // What the products throw for a bad request: answered as such (400/409), not as a server error.
        group.AddEndpointFilter(Api.ConsoleExceptionFilter.InvokeAsync);

        if (!project) group.MapAuthConfigEndpoint(options);

        {
            // Forced two-factor setup gate: a user the admin marked TwoFactorRequired who has not yet enrolled
            // is allowed only to log out, view state, edit profile, and finish enrolling — everything else 403s.
            group.AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                if (http.User.Identity?.IsAuthenticated == true)
                {
                    var user = await http.RequestServices.GetRequiredService<Identity.ConsoleAccounts>().CurrentAsync(http.User, http.RequestAborted);
                    if (user is { TwoFactorRequired: true, TwoFactorEnabled: false } && !IsTwoFactorSetupExempt(http.Request.Path)
                        && !Identity.ConsoleOidc.ProviderTwoStep(http, options))
                        return Results.Json(new { error = "two_factor_setup_required" }, statusCode: StatusCodes.Status403Forbidden);
                }
                return await next(ctx);
            });

            if (project) group.MapProjectAccessEndpoint();   // the caller's role in this project and what it has
            else
            {
                group.MapAuthEndpoints();
                if (options.ServerSettings is not null) Identity.ConsoleOidc.MapConsoleOidcEndpoints(group, options);
                group.MapTwoFactorEndpoints();
                group.MapUserEndpoints();
                group.MapInviteEndpoints();
            }
        }

        var isService = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (options.DatabaseTab is { } databaseTab)
        {
            if (isService?.IsService(typeof(DocumentDatabase)) == false)
                throw new InvalidOperationException(
                    "AddDatabaseTab() requires AddWincheDatabase() — the Database tab browses the document store.");
            group.MapConsoleDataEndpoints(databaseTab.MinRole);
            if (databaseTab.History)
            {
                var dbOptions = endpoints.ServiceProvider.GetService<Microsoft.Extensions.Options.IOptions<Winche.Database.DependencyInjection.WincheDatabaseOptions>>();
                var historyFromSettings = endpoints.ServiceProvider.GetServices<Winche.Core.SettingsArea>().Any(a => a.Name == Winche.Database.Settings.DatabaseSettings.Area);
                if (dbOptions?.Value.History is null && !historyFromSettings)
                    throw new InvalidOperationException(
                        "The Database tab's UseHistory() needs KeepHistory() in AddWincheDatabase — that's what records who changed what.");
                group.MapConsoleHistoryEndpoints(databaseTab.MinRole);
            }
        }
        if (options.StorageTab is { } storageTab)
        {
            if (isService?.IsService(typeof(FileStorage)) == false)
                throw new InvalidOperationException(
                    "AddStorageTab() requires AddWincheStorage() — the Storage tab browses the file store.");
            group.MapConsoleStorageEndpoints(storageTab.MinRole);
        }
        if (options.AssetsTab is not null)
        {
            if (isService?.IsService(typeof(Winche.Assets.WincheAssets)) == false)
                throw new InvalidOperationException("AddAssetsTab() requires AddWincheAssets() — the Assets page manages its releases.");
            group.MapConsoleAssetsEndpoints();
        }
        if (options.DatabaseRulesEditor is not null || options.StorageRulesEditor is not null || options.AssetsRulesEditor is not null)
        {
            group.MapConsoleRulesEndpoints();
        }

        // Database and Storage → Settings, when the packages use settings (o.UseSettings()).
        if (endpoints.ServiceProvider.GetServices<Winche.Core.SettingsArea>().Any(a => Api.ConsoleSettingsEndpoints.Areas.Contains(a.Name)))
        {
            group.MapConsoleSettingsEndpoints();
            if (isService?.IsService(typeof(Winche.Storage.Locations.StorageMoves)) == true) group.MapConsoleStorageLocationEndpoints();
        }

        // Hooks, when the app uses Winche.Webhooks (AddWincheHooks).
        if (isService?.IsService(typeof(Winche.Webhooks.HookCatalog)) == true) group.MapConsoleHooksEndpoints();
        // Background deliveries (hooks inside the app) of every package that has them.
        group.MapConsoleTokenEndpoints(options, signIn: !project);
        group.MapConsoleSecretsEndpoints();   // where secrets may be read from; references and the project's own keys
        if (!project)
        {
            Project.WincheProjectEndpoints.MapConsoleWincheProjectEndpoints(group, options);
            Project.ServerSettingsEndpoints.MapConsoleServerSettingsEndpoints(group, options);
        }
        // A project's own pages: not in the server's console, which has no project.
        if (!options.ServerConsole)
        {
            group.MapConsoleOverviewEndpoints(options);
            Project.ProjectEndpoints.MapConsoleProjectEndpoints(group);
            Project.DataLocationEndpoints.MapConsoleDataLocationEndpoints(group, options);
            Project.ProjectImageEndpoints.MapConsoleProjectImageEndpoints(group);
            Apps.RegisteredAppEndpoints.MapConsoleRegisteredAppEndpoints(group, options);
        }
        if (options.ProjectMembers) Project.ProjectMemberEndpoints.MapConsoleProjectMemberEndpoints(group);
        // Functions, when the app uses Winche.Functions (AddWincheFunctions).
        if (isService?.IsService(typeof(Winche.Functions.WincheFunctions)) == true)
        {
            group.MapConsoleFunctionsEndpoints();
            group.MapConsolePagesEndpoints();   // the project's own pages and their actions
        }

        if (options.AnyLogs)
        {
            group.MapConsoleLogsEndpoints(options);
        }

        if (options.AuthenticationTab)
        {
            var wincheAuth = isService?.IsService(typeof(Winche.Auth.IWincheAuth)) == true;
            var tokens = isService?.IsService(typeof(Winche.Auth.AspNetCore.WincheTokenProviders)) == true;
            group.MapConsoleAppAuthEndpoints(wincheAuth);
            group.MapConsoleAuthSettingsEndpoints(tokens);
        }

        // Server settings → Authentication: the same pages for the console's accounts (their Winche Auth), for server
        // admins. Their requests get the accounts' services first (users, settings, secrets, templates, keys).
        if (!project && options.Accounts is not null && options.ServerSettings is not null)
        {
            var accounts = group.MapGroup("").WithMetadata(new Identity.ConsoleAccountsEndpoint()).AddEndpointFilter(async (ctx, next) =>
                Project.WincheProjectsCaller.Of(ctx.HttpContext.User).ServerAdmin ? await next(ctx)
                    : Results.Json(new { error = "Only server admins manage the console's accounts." }, statusCode: StatusCodes.Status403Forbidden));
            accounts.MapConsoleAppAuthEndpoints(wincheAuth: true, root: "/server/auth");
            accounts.MapConsoleAuthSettingsEndpoints(tokens: false, root: "/server/auth");
            if (endpoints is IApplicationBuilder pipeline) pipeline.Use(Identity.ConsoleAccountsEndpoint.UseAccountsServicesAsync);
        }

        // Apps: how an app connects (the console's API; /.well-known/winche is MapWincheAppSetup).
        group.MapConsoleAppsEndpoints(options);

    }

    private static bool IsTwoFactorSetupExempt(PathString path)
    {
        var p = path.Value ?? "";
        return p.EndsWith("/session/logout", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/session/profile", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/session/2fa/setup", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/session/2fa/enable", StringComparison.OrdinalIgnoreCase);
    }
}
