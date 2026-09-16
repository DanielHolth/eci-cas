namespace EciCas.Agents.Toolkit;

/// <summary>
/// The manifest, made executable. Rather than a special case Intent or
/// ToolkitManagerAgent has to know about, "what can you do" is routed like
/// any other ask -- ToolkitManagerAgent matches it to this toolkit's own
/// trigger exemplars, ToolkitHandlerAgent dispatches to it, and it answers
/// through the ordinary ToolkitRequest/ToolkitResult round trip. Same
/// pipeline, no branch.
/// </summary>
public sealed class GuideToolkit(IToolkitCatalog catalog) : IToolkit
{
    public string Name => "guide";

    public Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        var entries = catalog.All
            .Where(d => !d.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
            .Select(d => $"- {d.Name}: {d.Description}");

        var capabilities = entries.Any()
            ? "Here's what I can reach right now:" + Environment.NewLine + string.Join(Environment.NewLine, entries)
            : "I don't have any toolkits available right now.";

        // Always both, not one or the other -- this is a background-routed
        // advisory Intent then composes a reply from (see
        // ToolkitManagerAgent.AdviceKey), not text shown verbatim, so the
        // fuller answer costs nothing when someone only asked "what can you
        // do" and matters a great deal when they asked "tell me about
        // yourself" and never see the keybindings otherwise.
        var text = MorrowGuide.AboutMorrow + Environment.NewLine + Environment.NewLine + capabilities;

        return Task.FromResult(new ToolkitOutcome(text, true, null));
    }
}
