using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Winche.Server.Tests;

/// <summary>
/// A tiny OpenID Connect provider on a real port: discovery, keys, an authorize endpoint that signs in whoever
/// <see cref="Next"/> says at once, and a token endpoint that checks the client secret and PKCE.
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "k1" };
    private readonly Dictionary<string, (string Nonce, string Challenge, string ClientId)> _codes = [];
    private readonly Dictionary<string, (string Sub, string Sid)> _refresh = [];

    public string Issuer { get; }
    public string ClientId { get; } = "winche-console";
    public string ClientSecret { get; } = "client-secret-1";

    /// <summary>Who signs in next: their claims (sub, email, groups, …).</summary>
    public Dictionary<string, object> Next { get; set; } = [];

    /// <summary>The provider session id of the last sign-in.</summary>
    public string? LastSid { get; private set; }

    /// <summary>Whether it hands out refresh tokens (as Keycloak does; some providers don't).</summary>
    public bool RefreshTokens { get; set; } = true;

    /// <summary>How many refreshes it has answered.</summary>
    public int Refreshes { get; private set; }

    /// <summary>Signs <paramref name="sub"/> out here: their refresh tokens stop working.</summary>
    public void SignOut(string sub)
    {
        lock (_refresh) foreach (var k in _refresh.Where(kv => kv.Value.Sub == sub).Select(kv => kv.Key).ToList()) _refresh.Remove(k);
    }

    /// <summary>A back-channel logout token for a session, or for everyone's sessions of a person.</summary>
    public string LogoutToken(string? sid, string? sub)
    {
        var claims = new Dictionary<string, object>
        {
            ["jti"] = Guid.NewGuid().ToString("n"),
            ["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>() },
        };
        if (sid is not null) claims["sid"] = sid;
        if (sub is not null) claims["sub"] = sub;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = ClientId, Claims = claims, IssuedAt = DateTime.UtcNow,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A token this provider signs about a workload (as a CI system does for a job): its subject and audience.</summary>
    public string WorkloadToken(string sub, string audience, TimeSpan? lifetime = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = audience, Claims = new Dictionary<string, object> { ["sub"] = sub }, IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromMinutes(5)), NotBefore = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });

    /// <summary>Claims for a signed access token (as Keycloak puts roles there); none: an opaque one.</summary>
    public Dictionary<string, object>? NextAccess { get; set; }

    public FakeOidcProvider()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer!, ["authorization_endpoint"] = $"{Issuer}/authorize", ["token_endpoint"] = $"{Issuer}/token",
            ["jwks_uri"] = $"{Issuer}/jwks", ["response_types_supported"] = new[] { "code" }, ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        }));
        _app.MapGet("/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Results.Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
        });
        _app.MapGet("/authorize", (string client_id, string redirect_uri, string state, string nonce, string code_challenge) =>
        {
            var code = Guid.NewGuid().ToString("n");
            lock (_codes) _codes[code] = (nonce, code_challenge, client_id);
            return Results.Redirect($"{redirect_uri}?code={code}&state={Uri.EscapeDataString(state)}");
        });
        _app.MapPost("/token", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            var basic = http.Request.Headers.Authorization.ToString();
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
            if (basic != expected) return Results.Json(new { error = "invalid_client", error_description = "Bad client credentials" }, statusCode: 401);
            if (form["grant_type"] == "refresh_token")
            {
                (string Sub, string Sid) session;
                lock (_refresh) if (!_refresh.Remove(form["refresh_token"].ToString(), out session)) return Results.Json(new { error = "invalid_grant", error_description = "Session not active" }, statusCode: 400);
                var rotated = "rt-" + Guid.NewGuid().ToString("n");
                lock (_refresh) _refresh[rotated] = session;
                Refreshes++;
                return Results.Json(new { access_token = "at", token_type = "Bearer", refresh_token = rotated });
            }
            (string Nonce, string Challenge, string ClientId) grant;
            lock (_codes) if (!_codes.Remove(form["code"].ToString(), out grant)) return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
            if (challenge != grant.Challenge) return Results.Json(new { error = "invalid_grant", error_description = "PKCE" }, statusCode: 400);
            var sid = Guid.NewGuid().ToString("n");
            LastSid = sid;
            var claims = new Dictionary<string, object>(Next) { ["nonce"] = grant.Nonce, ["sid"] = sid };
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = grant.ClientId, Claims = claims, Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            });
            var access = NextAccess is null ? "at" : new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = "account", Claims = NextAccess, Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            });
            if (!RefreshTokens) return Results.Json(new { access_token = access, token_type = "Bearer", id_token = token });
            var refresh = "rt-" + Guid.NewGuid().ToString("n");
            lock (_refresh) _refresh[refresh] = (Next["sub"].ToString()!, sid);
            return Results.Json(new { access_token = access, token_type = "Bearer", id_token = token, refresh_token = refresh });
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Issuer = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
