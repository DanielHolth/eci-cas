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

    /// <summary>
    /// How long a fact keeps half its importance for ranking purposes, in
    /// days. Zero switches decay off, and off is exactly today's behaviour.
    ///
    /// Nothing on disk changes -- this is a comparison key, not a write. The
    /// capsule keeps what was worth keeping; the persona surfaces what is
    /// still live. Sixty days is a season: a preference stated in spring is
    /// still worth a quarter of its importance in autumn, which is enough to
    /// beat filler and not enough to beat something said last week.
    ///
    /// Age is measured from the last time the row was touched, written or
    /// read, so a fact that keeps being recalled never decays. That is the
    /// line between forgetting something and merely having known it a while.
    /// </summary>
    public double SalienceHalfLifeDays { get; set; } = 60;

    /// <summary>
    /// What a row's hit rate is worth on top of its decayed importance.
    ///
    /// Added rather than multiplied, so a row nobody has asked for is not
    /// zeroed -- it simply gets no lift, and ranks on importance alone. The
    /// rate is hits over turns recorded and is capped at one, so this is the
    /// most any amount of use can be worth: a third of the way up the
    /// importance scale, enough to raise a low-scored fact the person keeps
    /// coming back to above a high-scored one nobody has ever wanted.
    /// </summary>
    public double SalienceHitWeight { get; set; } = 0.3;
}
