namespace EciCas.Agents.Librarian;

using EciCas.Core;

/// <summary>
/// A vector per (category, topic), so a turn can find its shelf by meaning
/// as well as by a model's judgment.
///
/// The pair layer is the coarse cut and it stays the coarse cut: this does
/// not replace the selection call, it runs beside it. The two fail in
/// different directions, which is the whole reason for keeping both. A
/// question phrased near a folder's own words ("what does my landlord
/// charge") is exactly what cosine is good at and exactly what a 4B selector
/// drops when forty options scroll past it. A question that names no folder's
/// words at all ("am I old enough to rent") is unreachable by embedding and
/// reachable by a model that knows what an age has to do with a lease.
///
/// What gets embedded is the gloss line - "rent, landlord, deposit, notice
/// period" - not the pair's name. The name is two words chosen for a file
/// system; the gloss is the sentence someone wrote to say what belongs in
/// there, and it is already the thing the selection prompt shows for the same
/// reason. A pair with no gloss falls back to its name, which is weak but
/// better than absent.
///
/// Cached against the model id: vectors from two embedders are not
/// comparable, and a tier switch has to invalidate the whole table rather
/// than silently mix. The table is small - one vector per pair, low hundreds
/// at most - and is rebuilt only when the index gains a pair.
/// </summary>
public sealed class PairVectorIndex(IEmbeddingProvider embeddings)
{
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _modelId = "";

    /// <summary>
    /// The pairs whose gloss sits closest to this turn, best first, at most
    /// <paramref name="take"/> of them. Empty whenever the embedder is
    /// unavailable or nothing clears the floor - both of which are ordinary,
    /// and both of which leave the selection call to do the whole job as it
    /// did before this existed.
    /// </summary>
    public async Task<IReadOnlyList<ArchivePair>> NearestAsync(
        float[] query,
        IReadOnlyList<ArchivePair> index,
        TopicGloss? gloss,
        int take,
        double minScore,
        CancellationToken cancellationToken)
    {
        if (!embeddings.Available || take <= 0 || query.Length == 0 || index.Count == 0)
        {
            return [];
        }

        await EnsureAsync(index, gloss, cancellationToken).ConfigureAwait(false);

        return
        [
            .. index
                .Select(p => (Pair: p, Score: _vectors.TryGetValue(KeyOf(p), out var v) ? VectorMath.Cosine(query, v) : double.NegativeInfinity))
                .Where(x => x.Score >= minScore)
                .OrderByDescending(x => x.Score)
                .Take(take)
                .Select(x => x.Pair),
        ];
    }

    private async Task EnsureAsync(IReadOnlyList<ArchivePair> index, TopicGloss? gloss, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_modelId, embeddings.ModelId, StringComparison.Ordinal))
            {
                _vectors.Clear();
                _modelId = embeddings.ModelId;
            }

            var missing = index.Where(p => !_vectors.ContainsKey(KeyOf(p))).Distinct().ToList();
            if (missing.Count == 0)
            {
                return;
            }

            // One batch for every new pair rather than one call each: the
            // first turn after a boot pays for the whole shelf, and every turn
            // after it pays for nothing.
            var vectors = await embeddings
                .EmbedAsync([.. missing.Select(p => TextFor(p, gloss))], EmbeddingKind.Passage, cancellationToken)
                .ConfigureAwait(false);

            if (vectors.Count != missing.Count)
            {
                return;
            }

            for (var i = 0; i < missing.Count; i++)
            {
                _vectors[KeyOf(missing[i])] = vectors[i];
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An embedder that fell over costs this turn its vector leads and
            // nothing else. Whatever was already cached stays cached.
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The gloss if the shelf defines one, otherwise the folder's own words.</summary>
    private static string TextFor(ArchivePair pair, TopicGloss? gloss) =>
        gloss?.For(pair.Category, pair.Topic) is { Length: > 0 } words
            ? $"{pair.Category} {pair.Topic}: {words}"
            : $"{pair.Category} {pair.Topic}";

    private static string KeyOf(ArchivePair pair) => $"{pair.Category}/{pair.Topic}";
}
