using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Winche.Console.Identity;

/// <summary>
/// Service accounts' access tokens (<c>wsa_…</c>): what a key or trusted identity is exchanged for, sent with each call.
/// They last an hour and are signed by the server (its data protection keys); each names the account, the key or
/// identity it came from, and the project's API, and is refused once either is revoked or the account turned off.
/// </summary>
public sealed class ServiceAccountTokens(IDataProtectionProvider protection)
{
    public const string Prefix = "wsa_";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private const string Purpose = "Winche.Console.ServiceAccounts.AccessToken.v1";

    private ITimeLimitedDataProtector Protector => protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>An access token for <paramref name="accountId"/>, from <paramref name="credential"/> (a key or identity id), for <paramref name="api"/>.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(string accountId, string credential, string api)
    {
        var expires = DateTimeOffset.UtcNow + Lifetime;
        return (Prefix + Protector.Protect($"{accountId}\n{credential}\n{api}", expires), expires);
    }

    /// <summary>The account and credential of a live (unexpired, untampered) token for <paramref name="api"/>, or null.</summary>
    public (string AccountId, string Credential)? Read(string token, string api)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var parts = Protector.Unprotect(token[Prefix.Length..]).Split('\n');
            return parts is [var account, var credential, var forApi] && forApi == api ? (account, credential) : null;
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// Checks a token a CI system or cloud host signed about itself (OpenID Connect) against an issuer's published keys,
/// for a service account's trusted identity: issuer, audience, expiry and signature.
/// </summary>
public sealed class TrustedIdentityValidator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonWebKeySet Keys)> _keys = new();

    /// <summary>The token's issuer, unchecked (to find the identities that trust it), or null when it isn't a JWT.</summary>
    public static string? IssuerOf(string token)
    {
        try { return new JsonWebTokenHandler().ReadJsonWebToken(token).Issuer; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The token's claims when it's signed by <paramref name="issuer"/> for <paramref name="audience"/> and live; else why not.</summary>
    public async Task<(IDictionary<string, object>? Claims, string? Problem)> ValidateAsync(string token, string issuer, string audience, CancellationToken ct)
    {
        JsonWebKeySet keys;
        try { keys = await KeysAsync(issuer, refresh: false, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        { return (null, $"Couldn't read {issuer}'s keys: {ex.Message}"); }
        var result = await Check(token, issuer, audience, keys);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            keys = await KeysAsync(issuer, refresh: true, ct);   // the issuer rotated its keys
            result = await Check(token, issuer, audience, keys);
        }
        return result.IsValid ? (result.Claims, null) : (null, result.Exception?.Message ?? "The token isn't valid.");
    }

    private static Task<TokenValidationResult> Check(string token, string issuer, string audience, JsonWebKeySet keys) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = audience, IssuerSigningKeys = keys.GetSigningKeys(),
            ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(60),
        });

    private async Task<JsonWebKeySet> KeysAsync(string issuer, bool refresh, CancellationToken ct)
    {
        if (!refresh && _keys.TryGetValue(issuer, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(15)) return cached.Keys;
        var discovery = JsonNode.Parse(await Http.GetStringAsync($"{issuer.TrimEnd('/')}/.well-known/openid-configuration", ct));
        var jwksUri = discovery?["jwks_uri"]?.GetValue<string>() ?? throw new InvalidOperationException("its discovery document has no jwks_uri");
        var keys = new JsonWebKeySet(await Http.GetStringAsync(jwksUri, ct));
        _keys[issuer] = (DateTimeOffset.UtcNow, keys);
        return keys;
    }
}
