using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace EciCas.Host.Startup;

/// <param name="Toolkits">A folder of manifests, relative to the pack.</param>
/// <param name="Config">A JSON file layered over the tier file.</param>
/// <param name="Theme">CSS token overrides the UI applies, e.g. <c>{"--accent": "#c84"}</c>.</param>
internal sealed record PackContributions(string? Toolkits = null, string? Config = null, IReadOnlyDictionary<string, string>? Theme = null);

/// <summary>
/// One folder under <c>Packs/</c> with a <c>pack.json</c>. Like a manifest,
/// <see cref="Approved"/> is only ever set by a person; an unapproved pack
/// contributes nothing. Its approval covers every toolkit it carries.
/// </summary>
internal sealed record PackManifest(
    string Name,
    int ApiVersion,
    IReadOnlyList<string>? Permissions = null,
    bool Approved = false,
    PackContributions? Contributes = null)
{
    public string Directory { get; init; } = string.Empty;
}

internal static class Packs
{
    public const int ApiVersion = 1;

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Packs");

    /// <summary>Safe mode (<c>--SafeMode=true</c> or env <c>SafeMode</c>) loads no pack at all, so a broken one can always be backed out of.</summary>
    public static bool SafeMode(IConfiguration configuration) => configuration.GetValue<bool>("SafeMode");

    /// <summary>Every pack on disk, approved or not, for the UI and the log.</summary>
    public static IReadOnlyList<PackManifest> Scan(Action<string> report)
    {
        if (!System.IO.Directory.Exists(Root))
        {
            return [];
        }

        var packs = new List<PackManifest>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Root, "pack.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(file), JsonSerializerOptions.Web);
                if (pack is null || string.IsNullOrWhiteSpace(pack.Name))
                {
                    report($"[packs] '{file}' has no name -- skipped.");
                    continue;
                }

                if (pack.ApiVersion != ApiVersion)
                {
                    report($"[packs] '{pack.Name}' targets apiVersion {pack.ApiVersion}; this Morrow speaks {ApiVersion} -- skipped.");
                    continue;
                }

                packs.Add(pack with { Directory = Path.GetDirectoryName(file)! });
            }
            catch (Exception failure) when (failure is JsonException or IOException)
            {
                report($"[packs] '{file}' failed to load: {failure.Message}");
            }
        }

        foreach (var pack in packs.Where(p => !p.Approved))
        {
            report($"[packs] '{pack.Name}' is not approved -- not loaded.");
        }

        return packs;
    }

    /// <summary>Approved packs, or none in safe mode.</summary>
    public static IReadOnlyList<PackManifest> Active(IConfiguration configuration, Action<string> report) =>
        SafeMode(configuration) ? [] : [.. Scan(report).Where(p => p.Approved)];

    /// <summary>Resolves a contributed path, refusing anything that climbs out of the pack's folder.</summary>
    public static string? Resolve(PackManifest pack, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(pack.Directory, relative));
        return full.StartsWith(Path.GetFullPath(pack.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
