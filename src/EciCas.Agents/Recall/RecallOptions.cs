namespace EciCas.Agents.Recall;

/// <summary>
/// Tier policy lives here, not in the store. A selected pair is never
/// truncated — a subtopic someone discusses at great length keeps all its
/// rows — so the knobs below shape the fan-out instead of the data.
/// </summary>
public sealed class RecallOptions
{
    /// <summary>
    /// How many candidate rows one picking call is shown. This is a quality
    /// limit, not a context-window one: a row costs well under 20 tokens, but
    /// a small non-reasoning model's ability to spot the relevant entry in a
    /// flat list falls off well before its context does. A pair holding more
    /// than this is split across that many more parallel workers.
    /// </summary>
    public int RowsPerWorker { get; set; } = 50;

    /// <summary>
    /// Ceiling on how many picking calls one turn may fan out into, across
    /// all selected pairs. Sized against ReasoningOptions.MaxSelectedPairs —
    /// roughly twice it, so an ordinary turn never hits the ceiling and only
    /// an unusually deep pair does. Its own knob rather than a derived value,
    /// so the fan-out can be tuned without touching selection.
    /// </summary>
    public int MaxConcurrentRecalls { get; set; } = 6;
}
