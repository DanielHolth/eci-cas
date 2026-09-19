namespace EciCas.Agents.Toolkit;

/// <summary>
/// A native action a toolkit manifest may name. Capabilities are registered
/// in code; a manifest picks one from that list and passes it options, and
/// never adds code of its own. That is the ceiling of what an approved
/// manifest -- hand-written, toolsmith-made or from a pack -- can do.
/// </summary>
public interface ICapability
{
    /// <summary>What a manifest's <c>verb.capability</c> names.</summary>
    string Name { get; }

    /// <summary>One line for the toolsmith's catalogue and the approval screen.</summary>
    string Description { get; }

    /// <summary>Checked against the tier's <see cref="ToolkitOptions.MaxRisk"/>, so a manifest cannot grant a tier something its capability is too risky for.</summary>
    CapabilityRisk Risk { get; }

    /// <summary>
    /// The options schema: <c>verb.options</c> must deserialize into this type
    /// with no unknown members. Null means the capability takes no options.
    /// </summary>
    Type? OptionsType => null;

    /// <summary>What the JSON shape alone cannot check (a URL being absolute, say). Null when fine.</summary>
    string? Validate(object? options) => null;

    Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken);
}

/// <summary>Ordered: a tier allows its <see cref="ToolkitOptions.MaxRisk"/> and everything below it.</summary>
public enum CapabilityRisk
{
    /// <summary>Touches only Morrow herself: her settings, her voice, her own catalogue.</summary>
    Local,

    /// <summary>Reaches a server outside this machine.</summary>
    Network,

    /// <summary>Runs code on this machine.</summary>
    System,
}

/// <param name="Command">The person's words that routed here.</param>
/// <param name="Options">The manifest's options, already deserialized into <see cref="ICapability.OptionsType"/>.</param>
/// <param name="Catalog">The live roster, for capabilities that describe or extend it (guide, toolsmith).</param>
public sealed record CapabilityCall(string Command, object? Options, IToolkitCatalog Catalog);
