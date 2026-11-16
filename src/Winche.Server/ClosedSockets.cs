using Winche.Auth;
using Winche.Database.AspNetCore.WebSockets.Connections;

namespace Winche.Server;

/// <summary>
/// A project's app user who is disabled or deleted (by an admin, by the app, or by a Keycloak sync) loses their open
/// database connections at once, on every server: a socket is signed in once, when it opens, and would otherwise keep
/// reading and writing until it happened to drop.
/// </summary>
internal sealed class ClosedSockets(WsConnectionRegistry sockets) : IWincheAuthObserver
{
    public string Name => "winche-server.closed-sockets";

    public Task OnUserEventAsync(WincheAuthUserEvent e, CancellationToken ct) => e.Type switch
    {
        WincheAuthUserEvent.Disabled => sockets.CloseUserConnectionsAsync(e.Uid, "account disabled", ct),
        WincheAuthUserEvent.Deleted => sockets.CloseUserConnectionsAsync(e.Uid, "account deleted", ct),
        _ => Task.CompletedTask,
    };
}
