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
/// </summary>
public sealed record Passage(
    string Id,
    string Text,
    IReadOnlyList<ArchivePair> Pairs,
    DateTimeOffset Timestamp,
    float[] Embedding);

/// <summary>
/// Shared-tier only, by construction: a self-critique belongs to the persona
/// the way the "self" and "system" categories already do, not to whoever
/// happened to be talking. So no profile parameter anywhere, and no
/// union-read.
/// </summary>
public interface IPassageStore
{
    /// <summary>Cosine top-K over every stored passage, best first, filtered by <paramref name="minScore"/>.</summary>
    Task<IReadOnlyList<PassageHit>> SearchAsync(float[] query, int topK, double minScore, CancellationToken cancellationToken);

    /// <summary>
    /// The most recently written passage — what the next batch revisits.
    /// Which means a thought is reachable for revision for exactly one
    /// batch and is then frozen. Selecting the revisit target by similarity
    /// instead is what would make the corpus a trail rather than a chain;
    /// see roadmap.md.
    /// </summary>
    Task<Passage?> LatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="added"/> and drops <paramref name="replacedId"/>
    /// in one pass. The revisit rewrites its subject in place rather than
    /// accumulating a second thought beside the first, so the corpus stays
    /// one passage per event-series.
    /// </summary>
    Task WriteAsync(IReadOnlyList<Passage> added, string? replacedId, CancellationToken cancellationToken);
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
