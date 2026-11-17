using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Options;

namespace Winche.Console.Identity;

internal static class ConsoleAuth
{
    /// <summary>
    /// Registers the console's sign-in: its accounts (a Winche Auth of their own, <see cref="ConsoleOptions.Accounts"/>), its
    /// tables (<see cref="ConsoleStore"/>), the console's cookie (named "WincheConsoleSession") WITHOUT changing the host's default
    /// scheme, and the three role policies. The host's own auth is untouched.
    /// </summary>
    public static IServiceCollection AddConsoleIdentity(this IServiceCollection services, ConsoleOptions options)
    {
        services.AddSingleton<ConsoleStore>();   // invites and providers' sign-outs
        services.AddDataProtection();
        services.AddSingleton<ConsoleInviteTokens>();
        services.AddSingleton<ConsoleAccounts>();   // the console's accounts: the host's Winche Auth for them (ConsoleOptions.Accounts)

        services.AddAuthentication()
            .AddCookie(ConsoleSession.Scheme, o =>
            {
                // A name of its own since 3.0: a cookie of an earlier server (path /) can't be taken for it.
                o.Cookie.Name = "WincheConsoleSession";
                if (options.SignInCookiePath is { } path) o.Cookie.Path = path;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = options.SessionLifetime;
                o.SlidingExpiration = true;
                // API semantics: status codes, not redirects.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                // Renewed with the accounts every few minutes; a session from an identity provider also lasts as long as the provider's.
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    await ConsoleSession.ValidateAsync(ctx);
                    if (ctx.Principal is not null) await ConsoleOidc.ValidateSessionAsync(ctx, options);
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(ConsoleRoles.SignedInPolicy, p => p
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme)
                .RequireAuthenticatedUser())
            // The server's own console has no project, so no project roles: there anyone signed in may use what a
            // Viewer or Member could (their own account, their projects); Admin is a server admin.
            .AddPolicy(ConsoleRoles.ViewerPolicy, p => (options.ServerConsole ? p.RequireAuthenticatedUser() : p.RequireRole(ConsoleRoles.Viewer, ConsoleRoles.Member, ConsoleRoles.Admin))
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme))
            .AddPolicy(ConsoleRoles.MemberPolicy, p => (options.ServerConsole ? p.RequireAuthenticatedUser() : p.RequireRole(ConsoleRoles.Member, ConsoleRoles.Admin))
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme))
            .AddPolicy(ConsoleRoles.AdminPolicy, p => p
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme)
                .RequireRole(ConsoleRoles.Admin));

        return services;
    }
}
