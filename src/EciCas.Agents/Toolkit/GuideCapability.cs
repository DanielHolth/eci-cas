namespace EciCas.Agents.Toolkit;

/// <summary>
/// The manifest, made executable. Rather than a special case Intent or
/// ToolkitManagerAgent has to know about, "what can you do" is routed like
/// any other ask -- ToolkitManagerAgent matches it to this toolkit's own
/// trigger exemplars, ToolkitHandlerAgent dispatches to it, and it answers
/// through the ordinary ToolkitRequest/ToolkitResult round trip. Same
/// pipeline, no branch.
/// </summary>
public sealed class GuideCapability : ICapability
{
    public string Name => "guide";

    public string Description => "Tells the person what Morrow is and lists the toolkits she can reach.";

    public CapabilityRisk Risk => CapabilityRisk.Local;

    public Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        var entries = call.Catalog.All
            .Where(d => call.Catalog.Find(d.Name) is not ManifestToolkit { Capability: GuideCapability })
            .Select(d => $"- {d.Name}: {d.Description}");

        var capabilities = entries.Any()
            ? "Here's what I can reach right now:" + Environment.NewLine + string.Join(Environment.NewLine, entries)
            : "I don't have any toolkits available right now.";

        // Always both, not one or the other -- this is a background-routed
        // advisory Intent then composes a reply from (see
        // ToolkitManager's report), not text shown verbatim, so the
        // fuller answer costs nothing when someone only asked "what can you
        // do" and matters a great deal when they asked "tell me about
        // yourself" and never see the keybindings otherwise.
        var text = MorrowGuide.AboutMorrow + Environment.NewLine + Environment.NewLine + capabilities;

        return Task.FromResult(new ToolkitOutcome(text, true, null));
    }
}
