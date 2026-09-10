namespace EciCas.Core;

/// <summary>
/// One self-contained thing that is true, in a sentence that stands alone.
///
/// **Why this is not an utterance.** "We moved to Bodø in 2019. It's colder
/// than Ingrid expected." is one utterance and two facts, and the second of
/// them cannot be stored as written -- a row reading "It's colder than Ingrid
/// expected" answers no question anyone will ever ask, because the pronoun
/// points at something that is no longer on the row. A fact has had its
/// antecedents put back: "Bodø is colder than Ingrid expected." That is what
/// makes "what is x?" retrievable from a paragraph that never asked it.
///
/// **Everything here is derived and therefore disposable.** The sentence, the
/// vector, the keywords, the thread, the supersession, the hit count -- all of
/// it is recomputable from <see cref="Utterance"/> via <see cref="SourceId"/>.
/// A bad extraction is a recomputation, not a legacy; delete the store and
/// backfill. That is the whole reason the model call on the write path is
/// affordable to be wrong about.
/// </summary>
public sealed record Fact(
    string Id,
    // The utterance this was read out of: the only link back to ground truth.
    string SourceId,
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
    /// Hits per turn since the fact appeared. The floor of ten turns keeps a
    /// row minted this morning from reading as the most useful thing in the
    /// archive because it was used once out of one.
    /// </summary>
    public double HitRate(long turnsNow) => HitCount / (double)Math.Max(turnsNow - FirstSeenTurn, 10);
}

/// <summary>
/// A partial write against a fact already on disk. Null means "no opinion",
/// which is what almost every caller has about most of these.
/// </summary>
public sealed record FactDerived(
    string Id,
    float[]? Embedding = null,
    string? EmbeddingModelId = null,
    string? ThreadId = null,
    string? SupersededBy = null);

/// <summary>
/// The index. Everything retrieval reads, and nothing retrieval reads lives
/// anywhere else.
///
/// Unlike <see cref="IUtteranceLog"/> this store is rewritten constantly --
/// vectors land late, threads land later, hit counts land forever. That is
/// exactly why it is a separate file: a bug in a whole-shard rewrite here
/// costs an index that can be rebuilt, and can no longer eat what was said.
/// </summary>
public interface IFactLog
{
    Task AppendAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken);

    Task<IReadOnlyList<Fact>> AllAsync(CancellationToken cancellationToken);

    Task UpdateDerivedAsync(IReadOnlyList<FactDerived> updates, CancellationToken cancellationToken);

    /// <summary>One increment per fact actually spliced into a prompt.</summary>
    Task RecordHitsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Throw the index away. The backfill's first move when it rebuilds from
    /// ground truth, and the reason a wrong extraction is never permanent.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the facts out of one thing somebody said.
///
/// One call per turn, not one per fact: the whole point of doing this with a
/// model rather than a regex is that the sentences resolve each other's
/// pronouns, and a per-fact call would have thrown away the context that made
/// them resolvable.
///
/// Implementations must never throw for content reasons. If the substrate is
/// down or the reply is unparseable, return the utterance verbatim as a single
/// fact -- an unsplit row retrieves badly, an absent row does not retrieve at
/// all, and the utterance is on disk either way to be backfilled from later.
/// </summary>
public interface IFactExtractor
{
    Task<IReadOnlyList<string>> ExtractAsync(Utterance utterance, CancellationToken cancellationToken);
}

/// <summary>
/// What the consolidator decided about a newly minted fact: which thread it
/// belongs to, and which older fact it makes obsolete.
/// </summary>
public sealed record ConsolidatorVerdict(string? ThreadId, string? Supersedes);

public interface IFactConsolidator
{
    Task<ConsolidatorVerdict?> AdjudicateAsync(Fact incoming, IReadOnlyList<Fact> candidates, CancellationToken cancellationToken);
}
