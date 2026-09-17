namespace EciCas.Core;

public enum ArchitectureLayer
{
    SharedCore,
    PlatformShell,
    RemoteRelay,
    SyncLayer,
}

public sealed record ArchitectureRule(
    ArchitectureLayer Layer,
    string Responsibility,
    IReadOnlyList<ArchitectureLayer> AllowedDependencies,
    IReadOnlyList<ArchitectureLayer> ForbiddenDependencies)
{
    public string Summary => Responsibility;
}

public static class ArchitectureContract
{
    public static ProductDirectionDefinition ProductDirection { get; } = new();

    public static IReadOnlyList<ArchitectureLayer> RequiredLayers { get; } =
    [
        ArchitectureLayer.SharedCore,
        ArchitectureLayer.PlatformShell,
        ArchitectureLayer.RemoteRelay,
        ArchitectureLayer.SyncLayer,
    ];

    private static readonly IReadOnlyDictionary<ArchitectureLayer, ArchitectureRule> Rules =
        new Dictionary<ArchitectureLayer, ArchitectureRule>
        {
            [ArchitectureLayer.SharedCore] = new(
                ArchitectureLayer.SharedCore,
                "Shared core: bus, agents, prompts, archive contracts, routing, and the memory model.",
                [ArchitectureLayer.SharedCore],
                []),
            [ArchitectureLayer.PlatformShell] = new(
                ArchitectureLayer.PlatformShell,
                "Platform shell: OS/window/input capture, desktop integration, and UI lifecycle.",
                [ArchitectureLayer.SharedCore],
                [ArchitectureLayer.RemoteRelay, ArchitectureLayer.SyncLayer]),
            [ArchitectureLayer.RemoteRelay] = new(
                ArchitectureLayer.RemoteRelay,
                "Remote relay: provider auth, secret management, and model gatewaying.",
                [ArchitectureLayer.SharedCore],
                [ArchitectureLayer.PlatformShell]),
            [ArchitectureLayer.SyncLayer] = new(
                ArchitectureLayer.SyncLayer,
                "Sync layer: Steam Cloud archive/state and durable user memory.",
                [ArchitectureLayer.SharedCore],
                [ArchitectureLayer.PlatformShell]),
        };

    public static bool IsSatisfied(IEnumerable<ArchitectureLayer> presentLayers)
    {
        var set = new HashSet<ArchitectureLayer>(presentLayers);
        return RequiredLayers.All(layer => set.Contains(layer));
    }

    public static IReadOnlyList<ArchitectureLayer> Validate(IEnumerable<ArchitectureLayer> presentLayers)
    {
        var set = new HashSet<ArchitectureLayer>(presentLayers);
        return RequiredLayers.Where(layer => !set.Contains(layer)).ToArray();
    }

    public static ArchitectureRule RuleFor(ArchitectureLayer layer) => Rules[layer];

    public static IReadOnlyList<string> ValidateDependencies(ArchitectureLayer layer, IEnumerable<ArchitectureLayer> presentDependencies)
    {
        var rule = RuleFor(layer);
        var set = new HashSet<ArchitectureLayer>(presentDependencies);

        var findings = new List<string>();

        var disallowed = rule.ForbiddenDependencies.Where(set.Contains).Select(dep =>
            $"{layer} depends on {dep}; this violates the split-layer contract and should be reviewed.");

        findings.AddRange(disallowed);

        return findings;
    }

    public static string ResponsibilityOf(ArchitectureLayer layer) => RuleFor(layer).Responsibility;
}

public sealed class ProductDirectionDefinition
{
    public string ClientRuntime { get; } = "Thick client on the user's machine";
    public string HostRuntime { get; } = "Cross-platform host runtime across Windows, macOS and Linux";
    public string SecretsLocation { get; } = "Remote relay owns API keys and provider secrets";
    public string DurableState { get; } = "Steam Cloud owns durable user state and the utterance parquet archive";
    public string LocalState { get; } = "Client-side local state remains device-scoped and transient";
}
