using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Realtime;
using Winche.Storage.Services;

namespace Winche.Server;

/// <summary>
/// Where a project's closed Realtime lists go when its settings ask for archiving: one file each in the project's
/// storage, <c>realtime/{path}/{closed at}.ndjson.gz</c>. Storage not started: the list stays for a later sweep.
/// </summary>
internal sealed class ProjectRealtimeArchive : IRealtimeArchive
{
    /// <summary>The project app's services, set once it's built.</summary>
    public IServiceProvider? Services { get; set; }

    public static string PathOf(RealtimeListInfo list) => "realtime/" + RealtimeArchives.FileName(list);

    public async Task ArchiveAsync(RealtimeListInfo list, Stream file, CancellationToken ct)
    {
        var services = Services ?? throw new InvalidOperationException("The project isn't running yet.");
        if (services.GetService<WincheProductReadiness>() is { } ready && !ready.IsReady(WincheProductNames.Storage))
            throw new InvalidOperationException("Archiving closed lists needs Storage: start it, and they're archived at the next sweep.");
        var metadata = new System.Text.Json.Nodes.JsonObject
        {
            ["list"] = list.Path, ["items"] = list.Count, ["closedAt"] = list.ClosedAt?.ToString("O"),
        };
        await services.GetRequiredService<FileStorage>().WriteAsync(PathOf(list), file, "application/gzip", file.Length, metadata, ct: ct);
    }
}
