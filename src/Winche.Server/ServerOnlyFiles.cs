using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.Authorization;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Server;

/// <summary>
/// Folders in a project's Storage that are the server's own (<c>realtime/</c>: Realtime's archives): app users never
/// reach them, whatever the Storage rules say. The console and functions, which use the server's rights, still do.
/// </summary>
internal static class ServerOnlyFiles
{
    private static readonly string[] Folders = ["realtime"];

    /// <summary>Whether <paramref name="path"/> is in, or is, a server-only folder.</summary>
    public static bool Covers(string path)
    {
        var first = path.Replace('\\', '/').TrimStart('/').Split('/', 2)[0];
        return Folders.Contains(first, StringComparer.Ordinal);
    }

    /// <summary>Wraps the app-facing storage, and the database's file references, so app users can't reach server-only folders.</summary>
    public static void Guard(IServiceCollection services)
    {
        Replace<IFileStorage>(services, inner => Wrap((IFileStorage)inner));
        var files = services.LastOrDefault(d => d.ServiceType == typeof(IWincheReferenceResolver) && d.ImplementationFactory is not null);
        if (files is not null)
        {
            services.Remove(files);
            services.AddSingleton<IWincheReferenceResolver>(sp => new GuardedReferences((IWincheReferenceResolver)files.ImplementationFactory!(sp)));
        }
    }

    /// <summary><paramref name="inner"/>, kept out of server-only folders.</summary>
    internal static IFileStorage Wrap(IFileStorage inner) => new GuardedStorage(inner);

    private static void Replace<T>(IServiceCollection services, Func<object, T> wrap) where T : class
    {
        var original = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(T), sp => wrap(original.ImplementationFactory is { } f ? f(sp)
            : original.ImplementationInstance ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)), original.Lifetime));
    }

    private static void Refuse(string path, string operation)
    {
        if (Covers(path)) throw new AccessDeniedException(path, operation);
    }

    private sealed class GuardedStorage(IFileStorage inner) : IFileStorage
    {
        public Task<FileRecord> SetAsync(string path, string mimeType, long sizeBytes, JsonObject? metadata = null,
            FilePrecondition? precondition = null, FileHeaders? headers = null, CancellationToken ct = default)
        { Refuse(path, "create"); return inner.SetAsync(path, mimeType, sizeBytes, metadata, precondition, headers, ct); }

        public Task<FileRecord?> GetAsync(string path, CancellationToken ct = default)
        { Refuse(path, "get"); return inner.GetAsync(path, ct); }

        public Task<FileRecord?> UpdateMetadataAsync(string path, JsonObject patch, FilePrecondition? precondition = null,
            FileHeaders? headers = null, CancellationToken ct = default)
        { Refuse(path, "update"); return inner.UpdateMetadataAsync(path, patch, precondition, headers, ct); }

        public Task<bool> DeleteAsync(string path, FilePrecondition? precondition = null, CancellationToken ct = default)
        {
            // The top (everything) would take the server's folders with it.
            if (path.Trim('/').Length == 0) throw new AccessDeniedException(path, "delete");
            Refuse(path, "delete");
            return inner.DeleteAsync(path, precondition, ct);
        }

        public Task<UploadSession> GenerateUploadUrlAsync(string path, CancellationToken ct = default)
        { Refuse(path, "create"); return inner.GenerateUploadUrlAsync(path, ct); }

        public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default)
        { Refuse(path, "get"); return inner.GenerateDownloadUrlAsync(path, ct); }

        public Task<FileRecord> ConfirmUploadAsync(string path, CancellationToken ct = default)
        { Refuse(path, "create"); return inner.ConfirmUploadAsync(path, ct); }

        public async Task<FileListResult> ListAsync(string directory, FileListOptions? options = null, CancellationToken ct = default)
        {
            if (directory.Trim('/').Length > 0) Refuse(directory, "list");
            var page = await inner.ListAsync(directory, options, ct);
            if (directory.Trim('/').Length > 0) return page;
            // The top: the server's folders aren't listed.
            return page with
            {
                Items = page.Items.Where(f => !Covers(f.Path)).ToList(),
                Prefixes = page.Prefixes.Where(p => !Covers(p)).ToList(),
            };
        }

        public Task<UploadSession> SignPartAsync(string path, int partNumber, CancellationToken ct = default)
        { Refuse(path, "create"); return inner.SignPartAsync(path, partNumber, ct); }

        public Task<IEnumerable<FilePart>> ListUploadedPartsAsync(string path, CancellationToken ct = default)
        { Refuse(path, "get"); return inner.ListUploadedPartsAsync(path, ct); }
    }

    /// <summary>A document's file reference into a server-only folder comes back as a bare path for app users.</summary>
    private sealed class GuardedReferences(IWincheReferenceResolver inner) : IWincheReferenceResolver
    {
        public string Kind => inner.Kind;
        public string? ChangeChannel => inner.ChangeChannel;

        public Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct) =>
            caller.Trusted ? inner.ResolveAsync(keys, caller, ct) : inner.ResolveAsync(keys.Where(k => !Covers(k)).ToList(), caller, ct);
    }
}
