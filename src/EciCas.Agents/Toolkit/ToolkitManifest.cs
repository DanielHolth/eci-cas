using System.Text.Json;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// A toolkit described in JSON: routing prose plus one native capability to
/// run. Every toolkit is one of these, the built-in ones included, so
/// descriptions, triggers and tier availability are tunable without a
/// rebuild. See Toolkits/README.md.
/// </summary>
/// <param name="Name">The toolkit's identity in routing, dispatch and the guide's listing.</param>
/// <param name="Description">Shown verbatim by the guide, same contract as <see cref="ToolkitDescriptor.Description"/>.</param>
/// <param name="Triggers">Prose exemplars ToolkitManagerAgent embeds and routes against.</param>
/// <param name="Verb">The capability this manifest runs, and its options.</param>
/// <param name="Tiers">Tiers this toolkit loads on, by name. Empty means every tier that has toolkits at all.</param>
/// <param name="Approved">
/// False until a human sets it: by hand, or with the Approve button in the
/// Toolkit tab, which shows the raw verb rather than the description.
/// Nothing that writes manifests on its own (the toolsmith, a pack install)
/// ever sets it.
/// </param>
public sealed record ToolkitManifest(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    ManifestVerb Verb,
    IReadOnlyList<string>? Tiers = null,
    bool Approved = false);

/// <param name="Capability">A registered <see cref="ICapability.Name"/>.</param>
/// <param name="Options">Must fit that capability's <see cref="ICapability.OptionsType"/>.</param>
public sealed record ManifestVerb(string Capability, JsonElement? Options = null);
