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
    /// all selected pairs. Sized against LibrarianOptions.MaxSelectedPairs —
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
    /// How many rows survive the cosine cut inside one pair. Sized at five
    /// because that is where it was measured: given the right file, the fact
    /// is in the vector top five 97% of the time, and a sixth row buys
    /// almost nothing while costing prompt room. Zero turns the row-vector
    /// layer off and restores the pre-vector read path exactly.
    ///
    /// This is not RowsPerWorker's replacement - a pair that cannot be
    /// narrowed still chunks by that. It is what makes the chunking
    /// unnecessary when it applies.
    /// </summary>
    public int VectorCandidates { get; set; } = 5;

    /// <summary>
    /// Whether vector-narrowed rows still go through a picking call.
    ///
    /// False, because it was measured: the picking stage was the biggest
    /// single read-side loss in the bench (batch 12 - not picking scored 78%
    /// against a lenient picking bar of 60%), and rows that cosine already
    /// ranked are exactly the rows it was most likely to throw away. A tier
    /// with a strong picking model and a reason to trust it can set this
    /// true; the calls it costs are real.
    /// </summary>
    public bool PickAfterVector { get; set; }
}
