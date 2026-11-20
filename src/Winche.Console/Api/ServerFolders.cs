namespace Winche.Console.Api;

/// <summary>
/// The folders the server writes to (projects' files, functions' downloads): whether it can, and what to do when it
/// can't. A folder mounted from the host keeps its owner there, often root, while the image runs as another user.
/// </summary>
public static class ServerFolders
{
    /// <summary>The user id this process runs as (the image's: 1654), or null where there's none to tell (Windows).</summary>
    public static string Uid => OperatingSystem.IsWindows() ? "?" : GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    /// <summary>
    /// What's wrong with <paramref name="folders"/>, a sentence each: each is made if missing, then written to and
    /// read back. Empty when every one works.
    /// </summary>
    public static IReadOnlyList<string> Problems(IEnumerable<string> folders)
    {
        var problems = new List<string>();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".winche-write-check-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                problems.Add(OperatingSystem.IsWindows()
                    ? $"{folder} isn't writable by this server ({ex.Message})."
                    : $"{folder} isn't writable by uid {Uid}, the user the server runs as ({ex.Message}). If it's a folder mounted from the host: chown -R {Uid}:{Uid} <that folder>.");
            }
        }
        return problems;
    }
}
