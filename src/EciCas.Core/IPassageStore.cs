namespace EciCas.Core;

/// <summary>
/// Turns text into a dense vector. Deliberately separate from
/// ISubstrateProvider: an embedding is not a completion, it has no tier, no
/// prompt and no cost model worth reporting per call, and the default
/// implementation is a local ONNX model rather than a remote one.
///
/// Unavailability is normal, not exceptional — the model file may not be
/// downloaded, or the API key may be missing — so callers check
/// <see cref="Available"/> and fall back to the pre-vector path rather than
/// catching. Same honesty posture as SubstrateHealth: a retrieval that
/// didn't happen must not look like one that found nothing.
/// </summary>
public interface IEmbeddingProvider
{
    bool Available { get; }

    /// <summary>
    /// Which model produced these vectors, stamped onto every passage row.
    /// Cosine returns 0.0 across a width mismatch, so swapping to a model of
    /// a different dimension silently retires the whole corpus; swapping at
    /// the same width is worse, because the old vectors keep scoring and
    /// stop meaning anything. Nothing else in the corpus ages a note out —
    /// no TTL, no size cap, no recency term — so a model change is the only
    /// event that can take a years-old note away, and it must not do it
    /// quietly. An empty string means "no embedder", which stamps nothing.
    /// </summary>
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// One retrievable prose chunk: a thought the persona had about a stretch of
/// turns — what those turns made it notice, written for no one — plus the
/// archive pairs that thought touches.
///
/// This is the *only* vector corpus. ArchiveRecords are never embedded and
/// never stored twice: a fact lives in exactly one pair file, and a passage
/// points at pairs rather than copying their rows. So the vectors index what
/// the persona made of the knowledge, not the knowledge itself — the two
/// substances stay apart, and a hit is a lead rather than an answer.
///
/// Pairs may be empty. A thought that touches no pair still surfaces its
/// text; it simply has nothing reality can check it against, which is the
/// asymmetry to remember when notes start agreeing with each other.
///
/// Timestamp is load-bearing, not bookkeeping: it reaches Intent as the age
/// on the front of the note, and a revisit preserves it so a sharpened
/// thought keeps the age of the thought it sharpens.
///
/// <para><b>Lineage.</b> Hindsight -> Intent's bundle -> Intent's reply ->
/// Reflection -> new note is a ring with no external grounding, and it will
/// not announce itself: a persona settling into a groove and a persona
/// developing a personality are the same observation from inside. These
/// three fields are how it is seen from the outside.</para>
///
/// <para><see cref="ParentIds"/> names the notes that were awake in the turns
/// this one was written about — empty means the thought came from turns the
/// persona had not already coloured. <see cref="EchoDepth"/> is the longest
/// such chain, one more than the deepest parent, zero with no parents; a
/// rising average across the corpus is the alarm. <see cref="Generation"/>
/// is the separate ring — how many times Reflection had reposted its own
/// idea as perception before this note — which the other two are silent
/// about, and which is silent about them.</para>
///
/// <para>Both are diagnostics and neither may ever weight retrieval. Prefer
/// a shallow note and the persona can lower its own echo depth by writing
/// thoughts with no history, which is the behaviour the number exists to
/// detect.</para>
///
/// <para>Rows written before these fields existed read back with no parents
/// and depth zero. That is not a claim they were grounded — it is the honest
/// default for an ancestry nobody recorded.</para>
/// </summary>
public sealed record Passage(
    string Id,
    string Text,
    IReadOnlyList<ArchivePair> Pairs,
    DateTimeOffset Timestamp,
    float[] Embedding,
    IReadOnlyList<string>? ParentIds = null,
    int EchoDepth = 0,
    int Generation = 0,
    string ModelId = "")
{
    public IReadOnlyList<string> ParentIds { get; init; } = ParentIds ?? [];
}

/// <summary>
/// Shared-tier only, by construction: a self-critique belongs to the persona
/// the way the "assistant" and "self" categories already do, not to whoever
/// happened to be talking. So no profile parameter anywhere, and no
/// union-read.
/// </summary>
public interface IPassageStore
{
    /// <summary>Cosine top-K over every stored passage, best first, filtered by <paramref name="minScore"/>.</summary>
    Task<IReadOnlyList<PassageHit>> SearchAsync(float[] query, int topK, double minScore, CancellationToken cancellationToken);

    /// <summary>
    /// The most recently written passage. No longer how the revisit target
    /// is chosen — Reflection picks the note nearest the batch, so a thought
    /// stays reachable however long ago it was written. This is the fallback
    /// for when there is no embedder or nothing clears the score floor, which
    /// is the old chain behaviour kept as a floor rather than as the rule.
    /// </summary>
    Task<Passage?> LatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="added"/> and drops <paramref name="replacedId"/>
    /// in one pass. The revisit rewrites its subject in place rather than
    /// accumulating a second thought beside the first, so the corpus stays
    /// one passage per event-series.
    /// </summary>
    Task WriteAsync(IReadOnlyList<Passage> added, string? replacedId, CancellationToken cancellationToken);

    /// <summary>
    /// Every distinct model id stamped on a stored passage, excluding rows
    /// written before the stamp existed. Read once at startup to decide
    /// whether the configured embedder agrees with the corpus it is about
    /// to search — see <see cref="IEmbeddingProvider.ModelId"/>.
    /// </summary>
    Task<IReadOnlyCollection<string>> StampedModelsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The startup check that a corpus and its embedder are the same pairing
/// they were when the corpus was written.
/// </summary>
public static class PassageCorpus
{
    /// <summary>
    /// Throws when the stored passages carry a model id the configured
    /// embedder does not share. Refusing to start is the conservative
    /// option, not the cautious-sounding one: the alternative is a host
    /// that runs happily while every note written before the swap either
    /// scores 0.0 forever (different width) or scores plausibly and means
    /// nothing (same width). Neither leaves a log line worth finding, and
    /// nothing else in the corpus ever removes a note, so a silent swap is
    /// the only way years of thought quietly stop being reachable.
    ///
    /// Rows written before the stamp existed are excluded by the caller and
    /// so pass: an unrecorded model is not a disagreeing one, and this must
    /// not brick a host over a corpus that predates the field.
    /// </summary>
    public static void EnsureModelAgreement(IReadOnlyCollection<string> stamped, string current)
    {
        // No embedder configured: nothing will search, so nothing can be
        // silently mis-scored. Unavailability is a normal state here.
        if (current.Length == 0)
        {
            return;
        }

        var foreign = stamped.Where(m => m != current).ToList();
        if (foreign.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Passage corpus was written by {string.Join(", ", foreign)} but the configured embedder is " +
            $"{current}. Those vectors cannot be compared, and starting anyway would retire every note " +
            "written before the change without saying so. Either restore the previous embedder, or " +
            "re-embed the corpus under the new one.");
    }
}

public sealed record PassageHit(Passage Passage, double Score);

public static class VectorMath
{
    /// <summary>
    /// Plain dot product: every vector this system stores is L2-normalized at
    /// write time, so the denominator is 1 and computing it every query is
    /// arithmetic nobody reads.
    /// </summary>
    public static double Cosine(float[] a, float[] b)
    {
        // Mismatched widths score zero, so a passage embedded by a different
        // model than the one asking simply never matches. That is the right
        // answer per call and a hazard in aggregate: swapping the embedding
        // model silently retires the entire corpus written before the swap,
        // and a same-width swap is worse — the old vectors still score, they
        // just no longer mean anything. Nothing here records which model
        // wrote a vector. Before any model change, that needs solving, or
        // months of notes leave without a log line. See roadmap.md.
        if (a.Length != b.Length)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Normalizes in place; a zero vector is left alone rather than producing NaNs.</summary>
    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm <= double.Epsilon)
        {
            return v;
        }

        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(v[i] / norm);
        }

        return v;
    }
}
