using Microsoft.Extensions.DependencyInjection;

namespace Winche.Console.Users;

/// <summary>A user as the Authentication tab shows them, from whichever directory holds them.</summary>
public sealed record DirectoryUser(
    string Uid, string? Email, bool EmailVerified, string? DisplayName, string? PhotoUrl, bool IsAnonymous, bool Disabled, bool MfaEnabled,
    IReadOnlyList<string> Providers, IReadOnlyDictionary<string, object?> CustomClaims, DateTimeOffset? CreatedAt, DateTimeOffset? LastSignInAt,
    DateTimeOffset? LockedOutUntil);

public sealed record DirectorySession(string Id, string Provider, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset? ExpiresAt,
    string? UserAgent, string? IpAddress);

public sealed record DirectoryUserDetail(DirectoryUser User, IReadOnlyList<DirectorySession> Sessions, int? RecoveryCodesLeft);

/// <summary>What a directory can do, so the console offers only that.</summary>
public sealed record DirectoryCapabilities(
    bool Create, bool Claims, string ClaimsLabel, bool Sessions, bool Unlock, bool ResetMfa, bool UnlinkProviders, bool PasswordReset,
    bool Verify, bool Delete, bool Disable);

/// <summary>An operation this directory doesn't support.</summary>
public sealed class DirectoryNotSupportedException(string message) : Exception(message);

/// <summary>A problem the directory reported, with a sentence for people.</summary>
public sealed class DirectoryException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The app's users as the Users tab manages them: Winche Auth's. People from a Keycloak with sync on are among them, and
/// Keycloak stays where they're managed; Winche Auth follows its changes (its Keycloak sync).
/// </summary>
public interface IUserDirectory
{
    /// <summary>wincheAuth.</summary>
    string Kind { get; }
    string Label { get; }
    DirectoryCapabilities Capabilities { get; }

    Task<(IReadOnlyList<DirectoryUser> Users, int Total)> ListAsync(string? search, int offset, int limit, CancellationToken ct);
    Task<DirectoryUserDetail?> GetAsync(string uid, CancellationToken ct);
    Task<DirectoryUser> CreateAsync(string? email, string? password, string? displayName, CancellationToken ct);
    Task<DirectoryUser> SetDisabledAsync(string uid, bool disabled, CancellationToken ct);
    Task<DirectoryUser> SetClaimsAsync(string uid, IReadOnlyDictionary<string, object?>? claims, CancellationToken ct);
    Task<int?> SignOutEverywhereAsync(string uid, CancellationToken ct);
    Task<bool> RevokeSessionAsync(string uid, string sessionId, CancellationToken ct);
    Task<DirectoryUser> UnlinkProviderAsync(string uid, string provider, CancellationToken ct);
    Task<DirectoryUser> ResetMfaAsync(string uid, CancellationToken ct);
    Task<DirectoryUser> UnlockAsync(string uid, CancellationToken ct);
    Task SendPasswordResetAsync(string uid, CancellationToken ct);
    Task SendVerificationAsync(string uid, CancellationToken ct);
    Task<DirectoryUser> SetEmailVerifiedAsync(string uid, bool verified, CancellationToken ct);
    Task DeleteAsync(string uid, CancellationToken ct);
}

/// <summary>The Users tab's directory: Winche Auth's users, when Winche Auth is in the app.</summary>
public sealed class UserDirectories(IServiceProvider services)
{
    public bool WincheAuthInstalled => services.GetService<IServiceProviderIsService>()?.IsService(typeof(Winche.Auth.IWincheAuth)) == true;

    /// <summary>The directory for this request, or null without Winche Auth.</summary>
    public Task<IUserDirectory?> CurrentAsync(IServiceProvider requestServices, CancellationToken ct) =>
        Task.FromResult<IUserDirectory?>(WincheAuthInstalled ? new WincheAuthDirectory(requestServices.GetRequiredService<Winche.Auth.IWincheAuth>()) : null);
}
