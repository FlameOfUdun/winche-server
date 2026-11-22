using Winche.Auth;

namespace Winche.Console.Users;

/// <summary>Users kept by Winche Auth in the project's own database.</summary>
internal sealed class WincheAuthDirectory(IWincheAuth auth) : IUserDirectory
{
    public string Kind => "wincheAuth";
    public string Label => "Winche Auth";
    public DirectoryCapabilities Capabilities { get; } = new(Create: true, Claims: true, ClaimsLabel: "Custom claims", Sessions: true, Unlock: true,
        ResetMfa: true, UnlinkProviders: true, PasswordReset: true, Verify: true, Delete: true, Disable: true, Profile: true, Email: true, Password: true, Photo: true);

    internal static DirectoryUser From(WincheUserInfo u) => new(u.Uid, u.Email, u.EmailVerified, u.DisplayName, u.PhotoUrl, u.IsAnonymous, u.Disabled,
        u.MfaEnabled, u.Providers, u.CustomClaims, u.CreatedAt, u.LastSignInAt, u.LockedOutUntil, u.PhotoSource);

    public async Task<(IReadOnlyList<DirectoryUser> Users, int Total)> ListAsync(string? search, int offset, int limit, CancellationToken ct)
    {
        var page = await auth.ListUsersAsync(search, offset, limit, ct);
        return ([.. page.Users.Select(From)], page.Total);
    }

    public async Task<DirectoryUserDetail?> GetAsync(string uid, CancellationToken ct)
    {
        if (await auth.GetUserAsync(uid, ct) is not { } u) return null;
        var sessions = (await auth.ListSessionsAsync(uid, ct))
            .Select(s => new DirectorySession(s.Id.ToString(), s.Provider, s.CreatedAt, s.LastUsedAt, s.ExpiresAt, s.UserAgent, s.IpAddress)).ToList();
        int? codes = u.MfaEnabled ? (await auth.GetMfaStatusAsync(uid, ct)).RecoveryCodesLeft : null;
        return new DirectoryUserDetail(From(u), sessions, codes);
    }

    public async Task<DirectoryUser> CreateAsync(string? email, string? password, string? displayName, CancellationToken ct) =>
        From(await auth.CreateUserAsync(email, password, displayName, ct));
    public async Task<DirectoryUser> SetDisabledAsync(string uid, bool disabled, CancellationToken ct) => From(await auth.SetDisabledAsync(uid, disabled, ct));
    public async Task<DirectoryUser> SetClaimsAsync(string uid, IReadOnlyDictionary<string, object?>? claims, CancellationToken ct) =>
        From(await auth.SetCustomClaimsAsync(uid, claims, ct));
    public async Task<int?> SignOutEverywhereAsync(string uid, CancellationToken ct) => await auth.RevokeSessionsAsync(uid, ct);
    public Task<bool> RevokeSessionAsync(string uid, string sessionId, CancellationToken ct) =>
        Guid.TryParse(sessionId, out var id) ? auth.RevokeSessionAsync(uid, id, ct) : Task.FromResult(false);
    public async Task<DirectoryUser> UnlinkProviderAsync(string uid, string provider, CancellationToken ct) => From(await auth.UnlinkProviderAsync(uid, provider, ct));
    public async Task<DirectoryUser> ResetMfaAsync(string uid, CancellationToken ct) => From(await auth.ResetMfaAsync(uid, ct));
    public async Task<DirectoryUser> UnlockAsync(string uid, CancellationToken ct) => From(await auth.UnlockAsync(uid, ct));

    public async Task SendPasswordResetAsync(string uid, CancellationToken ct)
    {
        var user = await auth.GetUserAsync(uid, ct);
        if (user?.Email is not { } email) throw new DirectoryException("This user has no email address.");
        await auth.SendPasswordResetAsync(email, AuthRequestInfo.Server, ct);
    }

    public Task SendVerificationAsync(string uid, CancellationToken ct) => auth.SendEmailVerificationAsync(uid, ct);
    public async Task<DirectoryUser> SetEmailVerifiedAsync(string uid, bool verified, CancellationToken ct) => From(await auth.SetEmailVerifiedAsync(uid, verified, ct));
    public Task DeleteAsync(string uid, CancellationToken ct) => auth.DeleteUserAsync(uid, ct);

    public async Task<DirectoryUser> UpdateProfileAsync(string uid, string? displayName, string? photoUrl, CancellationToken ct) =>
        From(await auth.UpdateProfileAsync(uid, displayName, photoUrl, ct));

    public async Task<DirectoryUser> PatchProfileAsync(string uid, Winche.Auth.ProfilePatch patch, CancellationToken ct) =>
        From(await auth.PatchProfileAsync(uid, patch, ct));

    public async Task<DirectoryUser> SetEmailAsync(string uid, string email, bool verified, bool sendVerification, CancellationToken ct)
    {
        var user = await auth.SetEmailAsync(uid, email, ct);   // at once, marked verified, sessions ended
        if (!verified && user.EmailVerified) user = await auth.SetEmailVerifiedAsync(uid, false, ct);
        if (!verified && sendVerification) await auth.SendEmailVerificationAsync(uid, ct);
        return From(user);
    }

    public Task RequestEmailChangeAsync(string uid, string email, CancellationToken ct) => auth.ChangeEmailAsync(uid, email, ct);

    public async Task<DirectoryUser> SetPhotoAsync(string uid, byte[] image, CancellationToken ct) => From(await auth.SetPhotoAsync(uid, image, ct));
    public async Task<DirectoryUser> RemovePhotoAsync(string uid, CancellationToken ct) => From(await auth.RemovePhotoAsync(uid, ct));

    public async Task<DirectoryUser> SetPasswordAsync(string uid, string password, CancellationToken ct) => From(await auth.SetPasswordAsync(uid, password, ct));
}
