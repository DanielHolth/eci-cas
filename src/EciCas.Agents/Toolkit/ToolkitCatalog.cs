using System.Text.Json;
using System.Text.Json.Serialization;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// One toolkit's routing and guide metadata: what to tell a person who asks
/// what Morrow can do, and what a turn has to resemble before
/// ToolkitManagerAgent routes to it. Write triggers as varied prose
/// phrasings, not keywords -- routing is by embedding.
/// </summary>
public sealed record ToolkitDescriptor(string Name, string Description, IReadOnlyList<string> Triggers);

/// <summary>
/// The live roster. <see cref="All"/> is replaced wholesale on a reload, so a
/// reader holding the old list keeps a consistent view and can tell by
/// reference that the roster moved.
/// </summary>
public interface IToolkitCatalog
{
    IReadOnlyList<ToolkitDescriptor> All { get; }

    IToolkit? Find(string name);
}

/// <summary>A fixed roster, for tests and hosts that load no manifests.</summary>
public sealed class ToolkitCatalog(IReadOnlyList<ToolkitDescriptor> all, IEnumerable<IToolkit>? toolkits = null) : IToolkitCatalog
{
    private readonly Dictionary<string, IToolkit> _toolkits = (toolkits ?? []).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolkitDescriptor> All { get; } = all;

    public IToolkit? Find(string name) => _toolkits.GetValueOrDefault(name);
}

/// <summary>A folder of manifests. <paramref name="Pack"/> is set for a pack's folder, whose approval covers every manifest in it.</summary>
public sealed record ManifestSource(string Directory, string? Pack = null);

public enum ManifestStatus
{
    /// <summary>Loaded and routable.</summary>
    Ready,

    /// <summary>Valid, waiting for a human to approve it.</summary>
    Pending,

    /// <summary>Approved, but this tier does not offer it.</summary>
    Unavailable,

    /// <summary>Did not parse or validate.</summary>
    Invalid,
}

public sealed record ManifestEntry(string File, string? Pack, ToolkitManifest? Manifest, ManifestStatus Status, string? Reason);

/// <summary>
/// Every manifest in the toolkit folder and in approved packs, validated
/// against the capability registry and gated by tier. Watches its folders
/// and reloads on change, so an edit, an approval or a toolsmith draft shows
/// up without a restart.
/// </summary>
public sealed class ManifestCatalog : IToolkitCatalog, IDisposable
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record Snapshot(IReadOnlyList<ToolkitDescriptor> All, IReadOnlyDictionary<string, IToolkit> Toolkits, IReadOnlyList<ManifestEntry> Entries);

    private readonly IReadOnlyList<ManifestSource> _sources;
    private readonly string _tier;
    private readonly ToolkitOptions _options;
    private readonly Action<string> _report;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private Snapshot _snapshot = new([], new Dictionary<string, IToolkit>(), []);

    public ManifestCatalog(IReadOnlyList<ManifestSource> sources, string tier, ToolkitOptions options, IEnumerable<ICapability> capabilities, Action<string> report, bool watch = true)
    {
        _sources = sources;
        _tier = tier;
        _options = options;
        _report = report;
        Capabilities = capabilities.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _debounce = new Timer(_ => Reload());

        Reload();

        if (!watch)
        {
            return;
        }

        foreach (var source in sources.Where(s => System.IO.Directory.Exists(s.Directory)))
        {
            var watcher = new FileSystemWatcher(source.Directory, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public IReadOnlyDictionary<string, ICapability> Capabilities { get; }

    /// <summary>Where hand-written and toolsmith manifests live; the first source.</summary>
    public string Directory => _sources[0].Directory;

    public IReadOnlyList<ToolkitDescriptor> All => _snapshot.All;

    public IReadOnlyList<ManifestEntry> Entries => _snapshot.Entries;

    public IToolkit? Find(string name) => _snapshot.Toolkits.GetValueOrDefault(name);

    public void Reload()
    {
        var entries = new List<ManifestEntry>();
        var toolkits = new Dictionary<string, IToolkit>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<ToolkitDescriptor>();

        foreach (var source in _sources.Where(s => System.IO.Directory.Exists(s.Directory)))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(source.Directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                var entry = Load(file, source, toolkits.Keys.Concat(entries.Where(e => e.Manifest is not null).Select(e => e.Manifest!.Name)), out var toolkit);
                entries.Add(entry);
                if (toolkit is not null)
                {
                    toolkits[toolkit.Name] = toolkit;
                    descriptors.Add(new ToolkitDescriptor(entry.Manifest!.Name, entry.Manifest.Description, entry.Manifest.Triggers));
                }
            }
        }

        _snapshot = new Snapshot(descriptors, toolkits, entries);

        foreach (var entry in entries.Where(e => e.Status is ManifestStatus.Pending or ManifestStatus.Invalid))
        {
            _report($"[toolkits] '{entry.File}' {(entry.Status == ManifestStatus.Pending ? "is pending approval -- not loaded" : $"failed to load: {entry.Reason}")}.");
        }
    }

    private ManifestEntry Load(string path, ManifestSource source, IEnumerable<string> taken, out IToolkit? toolkit)
    {
        toolkit = null;
        var file = Path.GetFileName(path);
        ToolkitManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ToolkitManifest>(File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception failure)
        {
            return new ManifestEntry(file, source.Pack, null, ManifestStatus.Invalid, $"couldn't parse: {failure.Message}");
        }

        if (manifest is null)
        {
            return new ManifestEntry(file, source.Pack, null, ManifestStatus.Invalid, "file is empty");
        }

        var error = Validate(manifest, out var options)
            ?? (taken.Contains(manifest.Name, StringComparer.OrdinalIgnoreCase) ? $"another manifest is already named \"{manifest.Name}\"" : null);
        if (error is not null)
        {
            return new ManifestEntry(file, source.Pack, manifest, ManifestStatus.Invalid, error);
        }

        if (!manifest.Approved && source.Pack is null)
        {
            return new ManifestEntry(file, source.Pack, manifest, ManifestStatus.Pending, null);
        }

        var gate = Gate(manifest);
        if (gate is not null)
        {
            return new ManifestEntry(file, source.Pack, manifest, ManifestStatus.Unavailable, gate);
        }

        toolkit = new ManifestToolkit(manifest, Capabilities[manifest.Verb.Capability], options, this);
        return new ManifestEntry(file, source.Pack, manifest, ManifestStatus.Ready, null);
    }

    /// <summary>Shape and capability checks, shared with the toolsmith so a draft fails the same way a file on disk would.</summary>
    public string? Validate(ToolkitManifest manifest, out object? options)
    {
        options = null;
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return "missing \"name\"";
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            return "missing \"description\"";
        }

        if (manifest.Triggers is not { Count: > 0 })
        {
            return "needs at least one entry in \"triggers\"";
        }

        if (manifest.Verb is null || string.IsNullOrWhiteSpace(manifest.Verb.Capability))
        {
            return "verb needs a \"capability\"";
        }

        if (!Capabilities.TryGetValue(manifest.Verb.Capability, out var capability))
        {
            return $"unknown capability \"{manifest.Verb.Capability}\" -- one of {string.Join(", ", Capabilities.Keys)}";
        }

        var given = manifest.Verb.Options is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element ? element : (JsonElement?)null;
        if (capability.OptionsType is null)
        {
            return given is null ? null : $"capability \"{capability.Name}\" takes no options";
        }

        try
        {
            options = given is { } json
                ? json.Deserialize(capability.OptionsType, StrictOptions)
                : JsonSerializer.Deserialize("{}", capability.OptionsType, StrictOptions);
        }
        catch (JsonException failure)
        {
            return $"options don't fit \"{capability.Name}\": {failure.Message}";
        }

        return capability.Validate(options);
    }

    private string? Gate(ToolkitManifest manifest)
    {
        if (!_options.Enabled)
        {
            return "toolkits are off on this tier";
        }

        if (manifest.Tiers is { Count: > 0 } tiers && !tiers.Contains(_tier, StringComparer.OrdinalIgnoreCase))
        {
            return $"not offered on the {_tier} tier";
        }

        var risk = Capabilities[manifest.Verb.Capability].Risk;
        return risk > _options.MaxRisk ? $"{manifest.Verb.Capability} is {risk} risk; the {_tier} tier allows up to {_options.MaxRisk}" : null;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _debounce.Dispose();
    }
}
