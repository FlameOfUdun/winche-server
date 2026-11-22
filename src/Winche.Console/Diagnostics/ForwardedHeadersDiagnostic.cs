using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Winche.Console.Diagnostics;

/// <summary>
/// One-time runtime guard for the single most common console deployment mistake: running behind a
/// TLS-terminating proxy (Cloudflare, an ALB, nginx, ...) without <c>app.UseForwardedHeaders(...)</c>. In
/// that state the request scheme stays "http" even though the browser is on https, so the console's
/// cookie/OIDC sign-in cookies never round-trip and login 401s immediately after authenticating.
///
/// The console deliberately does NOT configure forwarded headers itself: it is a host-wide, security-sensitive
/// concern (the trusted-proxy set depends on the deployment topology) and must run first in the pipeline —
/// none of which a library mapped under a route prefix can own. But it CAN turn an otherwise silent 401 into
/// one actionable log line.
/// </summary>
internal static class ForwardedHeadersDiagnostic
{
    private static int _warned;

    /// <summary>
    /// Warns (once per process) when the incoming request carries <c>X-Forwarded-Proto: https</c> yet the
    /// resolved scheme is still http — the fingerprint of a missing forwarded-headers middleware.
    /// </summary>
    public static void Inspect(HttpContext http)
    {
        if (Volatile.Read(ref _warned) != 0) return;

        var request = http.Request;
        if (request.IsHttps) return;                                                  // scheme already https — fine
        if (!request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)) return; // no proxy in front

        var forwardedHttps = proto.Any(v => v is not null
            && v.Split(',').Any(p => p.Trim().Equals("https", StringComparison.OrdinalIgnoreCase)));
        if (!forwardedHttps) return;

        if (Interlocked.Exchange(ref _warned, 1) != 0) return; // another thread beat us to it

        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Winche.Console")
            .LogWarning(
                "The console is behind a TLS-terminating proxy (X-Forwarded-Proto: {ForwardedProto}) but the request " +
                "scheme is '{Scheme}': the proxy's forwarded headers aren't believed, so console sign-in will fail " +
                "with 401 right after signing in. The proxy connected from {Proxy}. Winche Server believes proxies on " +
                "loopback and private networks; for one elsewhere (a cloud load balancer, a CDN) set " +
                "Winche__TrustedProxies__0 to its address or network (or * for any). A host of your own: register " +
                "app.UseForwardedHeaders(...) first, configured for your proxy.",
                proto.ToString(), request.Scheme, http.Connection.RemoteIpAddress?.ToString() ?? "an unknown address");
    }
}
