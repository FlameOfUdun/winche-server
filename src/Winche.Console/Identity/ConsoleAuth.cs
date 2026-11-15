using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Options;

namespace Winche.Console.Identity;

internal static class ConsoleAuth
{
    /// <summary>
    /// Registers the console's sign-in: its accounts (a Winche Auth of their own, <see cref="ConsoleOptions.Accounts"/>), its
    /// tables (<see cref="ConsoleStore"/>), the console's cookie (named "WincheConsole") WITHOUT changing the host's default
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
                o.Cookie.Name = "WincheConsole";
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
            .AddPolicy(ConsoleRoles.ViewerPolicy, p => p
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme)
                .RequireRole(ConsoleRoles.Viewer, ConsoleRoles.Member, ConsoleRoles.Admin))
            .AddPolicy(ConsoleRoles.MemberPolicy, p => p
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme)
                .RequireRole(ConsoleRoles.Member, ConsoleRoles.Admin))
            .AddPolicy(ConsoleRoles.AdminPolicy, p => p
                .AddAuthenticationSchemes(ConsoleSession.Scheme, ConsoleTokenHandler.Scheme)
                .RequireRole(ConsoleRoles.Admin));

        return services;
    }
}
