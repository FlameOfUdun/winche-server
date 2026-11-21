using static Winche.Console.Email.ConsoleLinks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Console.Email;
using Winche.Console.Identity;
using Winche.Console.Options;
using Winche.Console.Tabs;

namespace Winche.Console.Api;

public static class AuthEndpoints
{
    /// <param name="Cli">From <c>winche setup</c>: the CLI's name, to sign it in as the new admin (a session, like <c>winche login</c>'s).</param>
    public sealed record SetupRequest(string Email, string? FirstName, string? LastName, string Password, string? SetupCode = null, string? Cli = null);
    public sealed record SetupCodeRequest(string? SetupCode);
    public sealed record LoginRequest(string Email, string Password);
    public sealed record TwoFactorLoginRequest(string Code, bool? RememberMachine);
    public sealed record RecoveryLoginRequest(string RecoveryCode);
    public sealed record ProfileRequest(string? FirstName, string? LastName);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record ForgotPasswordRequest(string Email);
    /// <param name="Code">The code from the reset link (<c>?mode=resetPassword&amp;code=…</c>).</param>
    public sealed record ResetPasswordBody(string? Code, string? NewPassword);

    // manageUsers/invites/etc. describe the Identity provider (role-gated client-side); database/storage are
    // per-user (tab enabled AND the user's role meets its MinRole).
    private static object CapabilitiesFor(ConsoleRole? role, ConsoleOptions options, IServiceProvider sp) => options.ServerConsole
        ? new
        {
            // The server's console: accounts and server settings for its admins; the projects for everyone.
            manageUsers = role == ConsoleRole.Admin, invites = role == ConsoleRole.Admin, members = false,
            twoFactor = true, changePassword = true, editProfile = true, database = false, storage = false,
        }
        : new
    {
        // With project members, accounts are the server's: this project manages who's in it, not the accounts.
        manageUsers = !options.ProjectMembers, invites = !options.ProjectMembers, members = options.ProjectMembers,
        twoFactor = true, changePassword = true, editProfile = true,
        database = options.DatabaseTab is { } dt && role is { } r && r >= dt.MinRole,
        storage = options.StorageTab is { } st && role is { } r2 && r2 >= st.MinRole,
        logs = ConsoleLogsEndpoints.Capabilities(options, role),
        history = options.DatabaseTab is { History: true } ht && role is { } r3 && r3 >= ht.MinRole,
        authentication = ConsoleAppAuthEndpoints.Capability(options, role),
        settings = ConsoleSettingsEndpoints.Capabilities(sp, role),
        hooks = ConsoleHooksEndpoints.Capability(sp, role),
        functions = ConsoleFunctionsEndpoints.Capability(sp, role),
        assets = ConsoleAssetsEndpoints.Capability(options, role),
        consolePages = role is not null && sp.GetService<Winche.Functions.WincheFunctions>() is not null,
        // The Integrity page (the project's apps): anyone with a role, where Integrity is set up (project setup).
        integrity = role is not null && sp.GetService<Winche.Integrity.WincheIntegrity>() is { Available: true },
    };

    /// <summary>The account's role in this console: its project role with project members, else its own; null: no access.</summary>
    internal static async Task<string?> RoleHereAsync(ConsoleAccount account, ConsoleOptions options, IServiceProvider sp)
    {
        if (!options.ProjectMembers || account.IsAdmin) return account.Role;
        return await sp.GetRequiredService<Project.ProjectMembers>().RoleOfAsync(account.Id);
    }

    /// <summary>A sign-in waiting for its second step: the accounts' short-lived token, in a cookie of its own.</summary>
    private const string SecondStepCookie = "WincheConsoleSecondStep";
    private const string SecondStepPurpose = "Winche.Console.SecondStep.v1";

    private static Microsoft.AspNetCore.DataProtection.ITimeLimitedDataProtector SecondStep(HttpContext http) =>
        Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.ToTimeLimitedDataProtector(
            http.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector(SecondStepPurpose));

    /// <summary>Keeps a sign-in's second-step token for /login/2fa or /login/recovery (a short-lived cookie).</summary>
    internal static void AwaitSecondStep(HttpContext http, string mfaToken) =>
        http.Response.Cookies.Append(SecondStepCookie, Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.Protect(SecondStep(http), mfaToken, TimeSpan.FromMinutes(5)),
            new CookieOptions { Path = CookiePath(http), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5) });

    /// <summary>What a refused sign-in answers: the status the console's pages expect, and a sentence.</summary>
    private static IResult Refused(Winche.Auth.AuthException ex) => ex.Code switch
    {
        Winche.Auth.AuthErrorCodes.InvalidCredentials or Winche.Auth.AuthErrorCodes.InvalidMfaCode or Winche.Auth.AuthErrorCodes.UserNotFound => Results.Unauthorized(),
        Winche.Auth.AuthErrorCodes.UserDisabled => Results.Json(new { error = "Account is disabled." }, statusCode: StatusCodes.Status403Forbidden),
        Winche.Auth.AuthErrorCodes.LockedOut or Winche.Auth.AuthErrorCodes.TooManyAttempts =>
            Results.Json(new { error = "Account locked. Try again later." }, statusCode: StatusCodes.Status423Locked),
        _ => Results.BadRequest(new { error = ex.Message }),
    };

    /// <summary>Whether <paramref name="given"/> is the setup code (always, when the console doesn't ask for one).</summary>
    private static async Task<bool> SetupCodeOkAsync(ConsoleOptions options, string? given, CancellationToken ct)
    {
        if (options.SetupCode is null) return true;
        var expected = Normalize(await options.SetupCode(ct));
        var actual = Normalize(given ?? "");
        return actual.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(actual), System.Text.Encoding.UTF8.GetBytes(expected));
        static string Normalize(string code) => new([.. code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
    }

    /// <summary>
    /// A wrong setup code waits a second before saying so. Guessing a code this long is hopeless anyway; slowing it
    /// keeps it so without locking anyone out (behind a proxy every caller can look the same).
    /// </summary>
    private static async Task<IResult> SetupMissAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        return WrongSetupCode();
    }

    private static IResult WrongSetupCode() => Results.BadRequest(new { error = "That isn't the setup code. It's in the server's log (docker logs <container>): \"Setup code: …\"." });

    private static bool ResetEnabled(IServiceProvider sp, ConsoleOptions options) =>
        ConsoleEmail.Sender(sp) is not null && options.AllowSelfServicePasswordReset;

    /// <summary>
    /// <c>access</c>: the signed-in person's role in this project and what its console has (a project's part of what
    /// <c>session/state</c> says for the server). The person is the console's (<c>session/…</c>, the server's).
    /// </summary>
    public static IEndpointRouteBuilder MapProjectAccessEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/access", async (HttpContext http, ConsoleAccounts accounts, IServiceProvider sp, ConsoleOptions options) =>
        {
            var signedIn = await http.AuthenticateAsync(ConsoleSession.Scheme);
            if (!signedIn.Succeeded) signedIn = await http.AuthenticateAsync(ConsoleTokenHandler.Scheme);
            var user = signedIn.Succeeded ? await accounts.CurrentAsync(signedIn.Principal!, http.RequestAborted) : null;
            if (user is null) return Results.Json(new { error = "Sign in first." }, statusCode: StatusCodes.Status401Unauthorized);
            var role = await RoleHereAsync(user, options, sp);
            ConsoleRole? consoleRole = role switch
            {
                ConsoleRoles.Admin => ConsoleRole.Admin,
                ConsoleRoles.Member => ConsoleRole.Member,
                ConsoleRoles.Viewer => ConsoleRole.Viewer,
                _ => null,   // not a member of this project: nothing here
            };
            return Results.Json(new { role, member = role is not null, capabilities = CapabilitiesFor(consoleRole, options, sp) });
        });
        return app;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/session");

        // Anonymous: current session + whether the console has been initialized.
        auth.MapGet("/state", async (HttpContext http, ConsoleAccounts accounts, IServiceProvider sp, ConsoleOptions options) =>
        {
            var initialized = await accounts.AnyAsync(http.RequestAborted);
            var selfServiceResetEnabled = ResetEnabled(sp, options);
            // Not set up yet: whether setup asks for the code.
            var setup = initialized ? null : new { code = options.SetupCode is not null };
            // /state is anonymous, so http.User is not populated by the (no-default-scheme) auth middleware.
            var authResult = await http.AuthenticateAsync(ConsoleSession.Scheme);
            var principal = authResult.Succeeded ? authResult.Principal : null;
            // The identity providers on the sign-in page, each a button, beside email and password.
            var oidc = options.ServerSettings is { } server && server.ConsoleProviders.Any(p => p.Enabled)
                ? new
                {
                    providers = server.ConsoleProviders.Where(p => p.Enabled).Select(p => new { id = p.Id, label = p.Label }).ToList(),
                    passwordsOnlyForAdmins = server.ConsolePasswordsOnlyForAdmins,
                }
                : null;
            var user = principal is null ? null : await accounts.CurrentAsync(principal, http.RequestAborted);
            if (user is null)
                return Results.Json(new { provider = "identity", server = options.ServerConsole, capabilities = CapabilitiesFor(null, options, sp), initialized, setup, selfServiceResetEnabled, oidc, user = (object?)null });

            var viaProvider = principal!.HasClaim(Identity.ConsoleOidc.SignedInWithClaim, Identity.ConsoleOidc.LoginProvider);
            var providerTwoStep = Identity.ConsoleOidc.ProviderOf(principal, options) is { TrustProviderTwoStep: true };
            var role = await RoleHereAsync(user, options, sp);
            ConsoleRole? consoleRole = role switch
            {
                ConsoleRoles.Admin => ConsoleRole.Admin,
                ConsoleRoles.Member => ConsoleRole.Member,
                ConsoleRoles.Viewer => ConsoleRole.Viewer,
                _ => null,   // no role here (not a member of this project, or an account made for another one): nothing
            };
            return Results.Json(new
            {
                provider = "identity",
                // The server's own console (no project): the projects, the accounts and Server settings.
                server = options.ServerConsole,
                capabilities = CapabilitiesFor(consoleRole, options, sp),
                initialized,
                setup,
                selfServiceResetEnabled,
                oidc,
                user = new
                {
                    id = user.Id,
                    email = user.Email,
                    firstName = user.FirstName,
                    lastName = user.LastName,
                    role,
                    // Not a member of this project (project members): signed in, but nothing here. The server's
                    // console has no project: everyone signed in may use it (they see their projects).
                    member = options.ServerConsole || role is not null,
                    serverAdmin = user.IsAdmin,
                    twoFactorEnabled = user.TwoFactorEnabled,
                    twoFactorRequired = user.TwoFactorRequired,
                    mustSetupTwoFactor = user.TwoFactorRequired && !user.TwoFactorEnabled && !providerTwoStep,
                    signedInWithProvider = viaProvider,
                    hasPassword = user.HasPassword,
                },
            });
        });

        // First run: the setup code (from the server's log), checked before anything else is asked.
        auth.MapPost("/setup/code", async (SetupCodeRequest body, ConsoleAccounts accounts, ConsoleOptions options, HttpContext http, CancellationToken ct) =>
        {
            if (await accounts.AnyAsync(ct)) return Results.Conflict(new { error = "Already set up." });
            return await SetupCodeOkAsync(options, body.SetupCode, ct) ? Results.NoContent() : await SetupMissAsync(ct);
        });

        // First-run only: create the first account, a server admin with a password.
        auth.MapPost("/setup", async (SetupRequest body, ConsoleAccounts accounts, ConsoleOptions options, HttpContext http, CancellationToken ct) =>
        {
            if (await accounts.AnyAsync(ct)) return Results.Conflict(new { error = "Already initialized." });
            if (!await SetupCodeOkAsync(options, body.SetupCode, ct)) return await SetupMissAsync(ct);
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
                return Results.BadRequest(new { error = "Email and password are required." });
            try
            {
                var user = await accounts.CreateAsync(body.Email, body.Password, body.FirstName, body.LastName, ConsoleRoles.Admin, ct: ct);
                if (string.IsNullOrWhiteSpace(body.Cli)) return Results.Ok(new { email = user.Email });
                var name = body.Cli.Trim()[..Math.Min(body.Cli.Trim().Length, 80)];
                var t = await accounts.UseAsync(a => a.SignInAsUserAsync(user.Id, new Winche.Auth.AuthRequestInfo(http.Connection.RemoteIpAddress?.ToString(), name), ct));
                return Results.Ok(new { email = user.Email, session = new { accessToken = t.AccessToken, refreshToken = t.RefreshToken, expiresAt = t.ExpiresAt, user = t.User.Email ?? t.User.Uid } });
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Self-service password reset (anonymous). Available only when an email sender is registered
        // and self-service reset is allowed. Always returns 200 (never reveals whether the email exists).
        auth.MapPost("/forgot-password", async (ForgotPasswordRequest body, ConsoleAccounts accounts, IServiceProvider sp, ConsoleOptions options, HttpContext http) =>
        {
            var sender = ConsoleEmail.Sender(sp);
            if (sender is null || !options.AllowSelfServicePasswordReset) return Results.NotFound();
            if (await accounts.FindByEmailAsync(body.Email, http.RequestAborted) is { Active: true, Email: { } email } user)
            {
                var link = await accounts.UseAsync(a => a.CreateEmailLinkAsync(Winche.Auth.Email.AuthEmailKinds.ResetPassword, email, http.RequestAborted));
                await sender.SendPasswordResetAsync(new ConsoleEmailRecipient(email, user.FirstName, user.LastName), link, http.RequestAborted);
            }
            return Results.Ok();
        });

        // Sets a password with a reset link's code (a reset, or a new member's first password). Anonymous.
        auth.MapPost("/reset-password", async (ResetPasswordBody body, ConsoleAccounts accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Code)) return Results.BadRequest(new { error = "Invalid or expired link." });
            try
            {
                await accounts.UseAsync(a => a.ResetPasswordAsync(body.Code, body.NewPassword ?? "", ct));
                return Results.Ok();
            }
            catch (Winche.Auth.AuthException ex) when (ex.Code == Winche.Auth.AuthErrorCodes.WeakPassword) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Winche.Auth.AuthException) { return Results.BadRequest(new { error = "Invalid or expired link." }); }
        });

        auth.MapPost("/login", async (LoginRequest body, HttpContext http, ConsoleAccounts accounts, ConsoleOptions options) =>
        {
            var user = await accounts.FindByEmailAsync(body.Email, http.RequestAborted);
            if (user is null) return Results.Unauthorized();
            // With an identity provider for everyone, passwords are the server admins' way in when it's down.
            if (options.ServerSettings is { ConsolePasswordsOnlyForAdmins: true } server && !user.IsAdmin)
                return Results.Json(new { error = $"Use \"{server.ConsoleProviders.First(p => p.Enabled).Label}\"." }, statusCode: StatusCodes.Status403Forbidden);
            if (!user.Active) return Results.Json(new { error = "Account is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var tokens = await accounts.UseAsync(a => a.SignInAsync(user.Email!, body.Password ?? "", Info(http), http.RequestAborted));
                await ConsoleSession.SignInAsync(http, tokens);
                return Results.Ok(new { user = new { id = user.Id, email = user.Email, role = user.Role } });
            }
            catch (Winche.Auth.AuthException ex) when (ex.Code == Winche.Auth.AuthErrorCodes.MfaRequired && ex.Details?.GetValueOrDefault("mfaToken") is string token)
            {
                AwaitSecondStep(http, token);
                return Results.Ok(new { requiresTwoFactor = true });
            }
            catch (Winche.Auth.AuthException ex) { return Refused(ex); }
        });

        // The second step of a sign-in: an authenticator code, or a recovery code.
        async Task<IResult> SecondStepAsync(HttpContext http, ConsoleAccounts accounts, string? code)
        {
            string? token = null;
            if (http.Request.Cookies.TryGetValue(SecondStepCookie, out var raw))
                try { token = Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.Unprotect(SecondStep(http), raw, out _); } catch (System.Security.Cryptography.CryptographicException) { }
            if (token is null) return Results.Unauthorized();
            try
            {
                var tokens = await accounts.UseAsync(a => a.SignInWithMfaAsync(token, (code ?? "").Replace(" ", "").Replace("-", ""), Info(http), http.RequestAborted));
                http.Response.Cookies.Delete(SecondStepCookie, new CookieOptions { Path = CookiePath(http) });
                await ConsoleSession.SignInAsync(http, tokens);
                var user = ConsoleAccounts.From(tokens.User);
                return Results.Ok(new { user = new { id = user.Id, email = user.Email, role = user.Role } });
            }
            catch (Winche.Auth.AuthException ex) { return Refused(ex); }
        }
        auth.MapPost("/login/2fa", (TwoFactorLoginRequest body, HttpContext http, ConsoleAccounts accounts) => SecondStepAsync(http, accounts, body.Code));
        auth.MapPost("/login/recovery", (RecoveryLoginRequest body, HttpContext http, ConsoleAccounts accounts) => SecondStepAsync(http, accounts, body.RecoveryCode));

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await ConsoleSession.SignOutAsync(http);
            return Results.Ok();
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);

        // Self-service: any authenticated user manages their own name + password.
        auth.MapPut("/profile", async (ProfileRequest body, ConsoleAccounts accounts, HttpContext http) =>
        {
            if (await accounts.CurrentAsync(http.User, http.RequestAborted) is not { } user) return Results.Unauthorized();
            await accounts.UpdateAsync(user.Id, firstName: body.FirstName ?? "", lastName: body.LastName ?? "", ct: http.RequestAborted);
            return Results.Ok();
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);

        auth.MapPost("/password", async (ChangePasswordRequest body, ConsoleAccounts accounts, HttpContext http) =>
        {
            if (await accounts.CurrentAsync(http.User, http.RequestAborted) is not { } user) return Results.Unauthorized();
            try
            {
                await accounts.UseAsync(a => a.ChangePasswordAsync(user.Id, body.CurrentPassword ?? "", body.NewPassword ?? "", Info(http), http.RequestAborted));
                return Results.Ok();
            }
            catch (Winche.Auth.AuthException ex) { return Results.BadRequest(new { error = ex.Code == Winche.Auth.AuthErrorCodes.InvalidCredentials ? "Incorrect password." : ex.Message }); }
        }).RequireAuthorization(ConsoleRoles.ViewerPolicy);

        return app;
    }

    /// <summary>Where a sign-in comes from, for the account's sessions list.</summary>
    internal static Winche.Auth.AuthRequestInfo Info(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null);
}
