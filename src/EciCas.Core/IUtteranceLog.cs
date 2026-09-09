namespace EciCas.Core;

/// <summary>
/// One thing somebody said, and what has since been worked out about it.
///
/// The first five fields are ground truth (docs/roadmap.md, "Ground truth,
/// and everything else"): the original utterance, when, who, which person's
/// archive it belongs to, and the deterministic keyword set. Appended, never
/// rewritten, readable in 2126 by anyone with a parquet reader and none of
/// our code.
///
/// Everything after <see cref="Embedding"/> is derived and disposable --
/// rebuildable from the text alone by a background pass. That line is what
/// makes the rest of the design safe to be wrong about: a bad thread in 2030
/// is a recomputation, not a legacy.
/// </summary>
/// <param name="Keywords">
/// Deterministic: tokenise, drop stopwords, keep what is rare in the corpus
/// with casing and digits as a complement. Ground truth even though it is
/// computed, because it is computed without a model and the extractor is
/// part of the format -- and because the consolidator's shortcut reads it,
/// so it has to be there before any judgment is.
/// </param>
/// <param name="ThreadId">
/// Which running subject this belongs to, minted by the write-time cosine
/// sweep. Null means the sweep has not run over this row yet, which is what
/// a freshly appended row looks like for as long as the backfill takes.
/// </param>
/// <param name="SupersededBy">
/// The id of the utterance that replaced this one's value at the same
/// subject. Set by the consolidator, never by cosine: deciding that a Tesla
/// replaces a Subaru and a wife's Volvo does not is the reading problem the
/// call exists for. A row carrying this is invisible to <em>Find</em> and in
/// scope for <em>Characterise</em>.
/// </param>
/// <param name="HitCount">
/// Numerator only. The rate is <c>HitCount / (turnsNow - FirstSeenTurn)</c>,
/// computed at read time from the log's global counter, so a turn writes one
/// integer in one place rather than a column across every row it did not
/// touch.
/// </param>
public sealed record Utterance(
    string Id,
    string Text,
    DateTimeOffset Timestamp,
    string Speaker,
    string? ProfileId,
    IReadOnlyList<string> Keywords,
    float[]? Embedding = null,
    string EmbeddingModelId = "",
    string? ThreadId = null,
    string? SupersededBy = null,
    int HitCount = 0,
    long FirstSeenTurn = 0)
{
    public bool HasVector(string modelId) =>
        Embedding is { Length: > 0 } && string.Equals(EmbeddingModelId, modelId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Frequency as a rate rather than a count, so a row that has existed
    /// for a decade is not rewarded for its age. Ten opportunities is the
    /// floor on the denominator: a row first seen this turn would otherwise
    /// divide by nothing and outrank the whole archive.
    /// </summary>
    public double HitRate(long turnsNow) => HitCount / (double)Math.Max(turnsNow - FirstSeenTurn, 10);
}

/// <summary>
/// The derived columns a pass wants to write back, keyed by utterance id.
/// One record rather than one call per column, because the log rewrites a
/// whole shard per write and a threading pass touches thousands of rows.
/// </summary>
public sealed record UtteranceDerived(
    string Id,
    float[]? Embedding = null,
    string? EmbeddingModelId = null,
    string? ThreadId = null,
    string? SupersededBy = null);

/// <summary>
/// Append-only log of utterances, sharded by month. The whole read path is a
/// sweep over every shard -- nothing routes on one (roadmap, "Time shards,
/// not importance tiers"), so a shard is a container and never a decision.
/// </summary>
public interface IUtteranceLog
{
    /// <summary>Ground truth in, nothing out. Never updates, never deletes.</summary>
    Task AppendAsync(IReadOnlyList<Utterance> utterances, CancellationToken cancellationToken);

    /// <summary>
    /// Everything, oldest first. The corpus is held in memory because every
    /// query is a cosine sweep over all of it; at a hundred thousand rows
    /// that is forty megabytes of vector and a few milliseconds of dot
    /// product, and no index has to be right.
    /// </summary>
    Task<IReadOnlyList<Utterance>> AllAsync(CancellationToken cancellationToken);

    /// <summary>Write derived columns back. Ground-truth fields are ignored if passed.</summary>
    Task UpdateDerivedAsync(IReadOnlyList<UtteranceDerived> updates, CancellationToken cancellationToken);

    /// <summary>Bump <see cref="Utterance.HitCount"/> for rows a read actually surfaced.</summary>
    Task RecordHitsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    /// <summary>
    /// The global turn counter, kept outside the shards -- the denominator of
    /// every hit rate, and the ordinal a new row stamps as its first-seen.
    /// </summary>
    long TurnsRecorded { get; }

    Task RecordTurnAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The consolidator's answer: which thread the new utterance joins, and
/// whether it retires the row it joined.
///
/// Both may be null. A null <see cref="ThreadId"/> is "none of these" and the
/// caller mints; a null <see cref="Supersedes"/> is "both still true", which
/// is the common case -- "I love hiking" and "I hiked Besseggen" are one
/// subject and neither cancels the other.
/// </summary>
public sealed record ConsolidatorVerdict(string? ThreadId, string? Supersedes);

/// <summary>
/// The one model call the inversion kept, and the only place in the write
/// path where judgment happens.
///
/// It is handed a new utterance and the five nearest thread representatives
/// and asked a question cosine cannot answer: are these the same running
/// subject, and if so does the new one replace the old. Everything else --
/// the sweep, the threshold, the verbatim shortcut -- exists to make sure
/// this is asked rarely and asked well.
///
/// Off the turn's critical path, and gated, so a tier without it degrades to
/// deterministic threading rather than to nothing.
/// </summary>
public interface IUtteranceConsolidator
{
    Task<ConsolidatorVerdict?> AdjudicateAsync(Utterance incoming, IReadOnlyList<Utterance> candidates, CancellationToken cancellationToken);
}
