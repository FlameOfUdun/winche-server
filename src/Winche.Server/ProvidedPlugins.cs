using System.Text.Json.Nodes;
using Winche.Functions;

namespace Winche.Server;

/// <summary>
/// The plugin packages in <see cref="ServerOptions.FunctionPluginsPath"/>: every folder below it with a package.json and a
/// winche-plugin.json, by name (its newest version) and by name@version (each one), so copies on older versions keep
/// working while a newer one is offered.
/// </summary>
internal static class ProvidedPlugins
{
    public static void Add(FunctionsOptions o, string root)
    {
        var found = new List<(string Name, string Version, string Dir)>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var package = Path.Combine(dir, "package.json");
            if (!File.Exists(Path.Combine(dir, PluginManifest.FileName)) || !File.Exists(package)) continue;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(package));
                if (json?["name"]?.GetValue<string>() is { Length: > 0 } name && json["version"]?.GetValue<string>() is { Length: > 0 } version)
                    found.Add((name, version, dir));
            }
            catch (System.Text.Json.JsonException) { }
        }
        foreach (var group in found.GroupBy(f => f.Name))
        {
            foreach (var f in group) o.ProvidedPlugins[$"{f.Name}@{f.Version}"] = f.Dir;
            o.ProvidedPlugins[group.Key] = group.OrderByDescending(f => Parse(f.Version)).First().Dir;
        }
    }

    private static Version Parse(string v) => System.Version.TryParse(v.Split('-')[0], out var x) ? x : new Version(0, 0);
}
