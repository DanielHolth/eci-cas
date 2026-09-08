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
    /// all selected pairs. Sized against the lane count RuntimeKnobs derives —
    /// roughly twice it, so an ordinary turn never hits the ceiling and only
    /// an unusually deep pair does. Its own knob rather than a derived value,
    /// so the fan-out can be tuned without touching selection.
    /// </summary>
    public int MaxConcurrentRecalls { get; set; } = 6;

    /// <summary>
    /// How many rows one picking call may hand back, and — because the
    /// picking call is skipped entirely when a turn loads no more rows than
    /// this — also the size below which a young archive passes whole without
    /// a substrate call at all. Was a const in RecallAgent; it is per-tier
    /// config because it, not the pair count, decides how many addresses
    /// reach Intent, and the ceiling is this times MaxConcurrentRecalls.
    /// </summary>
    public int MaxPickedPerWorker { get; set; } = 5;

    /// <summary>
    /// How many rows of the recency lane one turn puts in front of the
    /// picking model. The lane spans a year, so this is what makes it a
    /// prompt rather than a dump: the newest N, one dedicated call, always
    /// made whatever Librarian selected.
    ///
    /// Sized like RowsPerWorker and for the same reason - it is one worker
    /// is budget - but separately, because the lane is one call a turn while
    /// the pairs are as many as their depth warrants.
    /// </summary>
    public int RecentRows { get; set; } = 30;

    /// <summary>
    /// Recalls a turn may fire, the recency lane included — the tier's
    /// starting value for the Recall-threads knob, which is what actually
    /// governs. One is the lane alone; every thread after it is one pair,
    /// alternating vector lane then selector lane.
    ///
    /// This replaced LibrarianOptions.MaxSelectedPairs and VectorPairs,
    /// which were two budgets for one question. A person tuning this is
    /// asking "how many calls may a turn cost", and answering that in two
    /// places meant neither number could be read as the answer.
    /// </summary>
    public int Threads { get; set; } = 3;

    /// <summary>
    /// Whether vector-narrowed rows still go through a picking call.
    ///
    /// True now, and the measurement that said otherwise has not been
    /// contradicted -- it was answering a different question. When the cosine
    /// cut kept fewer rows than a worker was allowed to return, picking could
    /// only subtract, and the bench duly measured it subtracting the right
    /// answer (batch 12: 78% without it against a lenient 60% bar). The cut
    /// is three times the depth now, so the picking call is what turns thirty
    /// candidates into the ten a turn actually carries. Setting this false
    /// hands all thirty to Intent instead.
    /// </summary>
    public bool PickAfterVector { get; set; } = true;
}
