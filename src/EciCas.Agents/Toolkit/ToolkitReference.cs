namespace EciCas.Agents.Toolkit;

/// <summary>
/// One source a toolkit consulted: what a person could click to check the
/// answer for themselves. Carried beside a <see cref="ToolkitOutcome"/>'s
/// prose so the References log never has to parse it back out of text.
/// </summary>
public sealed record ToolkitReference(string Title, string Url, string Summary);
