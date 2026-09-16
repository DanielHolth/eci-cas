namespace EciCas.Agents.Toolkit;

/// <summary>
/// One toolkit's description for the two things nothing else can derive from
/// <see cref="IToolkit"/> alone: what to tell a person who asks what Morrow
/// can do, and what a turn has to resemble before ToolkitManagerAgent will
/// route to it. Kept separate from <see cref="IToolkit"/> itself rather than
/// grown onto that interface so a toolkit implementation never has to import
/// ToolkitManagerAgent's routing concerns to exist.
/// </summary>
/// <param name="Name">Matches the paired <see cref="IToolkit.Name"/> exactly -- see <see cref="Startup.ToolkitRegistration"/>, which registers both together.</param>
/// <param name="Description">One or two sentences, written for a person asking what Morrow can do. Rendered verbatim by GuideToolkit.</param>
/// <param name="Triggers">
/// Prose exemplars of the kinds of asks this toolkit answers -- not commands,
/// not keywords. Embedded once and cached; a turn is routed here when it
/// lands closer to one of these than to any other toolkit's, and clears
/// <see cref="ToolkitOptions.RouteFloor"/>. Write a handful of varied
/// phrasings, not a single canonical one -- the whole point of routing by
/// embedding instead of substring match is that "clean up my downloads
/// folder" and "free up some disk space" should both land here without
/// sharing a word.
/// </param>
public sealed record ToolkitDescriptor(string Name, string Description, IReadOnlyList<string> Triggers);

/// <summary>The whole toolkit roster's metadata, read by GuideToolkit (to describe it) and ToolkitManagerAgent (to route to it).</summary>
public interface IToolkitCatalog
{
    IReadOnlyList<ToolkitDescriptor> All { get; }
}

public sealed class ToolkitCatalog(IReadOnlyList<ToolkitDescriptor> all) : IToolkitCatalog
{
    public IReadOnlyList<ToolkitDescriptor> All { get; } = all;
}
