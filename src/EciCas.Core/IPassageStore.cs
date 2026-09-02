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
/// One retrievable prose chunk: the persona's own 5-15 word critique of an
/// event-series — what context it wishes it had had — plus the archive pairs
/// it wished it had read.
///
/// This is the *only* vector corpus. ArchiveRecords are never embedded and
/// never stored twice: a fact lives in exactly one pair file, and a passage
/// points at pairs rather than copying their rows. So the vectors index the
/// persona's judgement about retrieval, not the knowledge itself — which is
/// what makes a hit a learned shortcut ("last time this came up I should
/// have read person/identity") instead of a second, drifting copy of the
/// archive.
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

    /// <summary>The most recently written passage — what the next batch revisits.</summary>
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
