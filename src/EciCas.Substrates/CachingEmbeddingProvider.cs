using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Substrates;

/// <summary>
/// Remembers the last few texts embedded, so the same string embedded twice
/// in a turn costs one model pass instead of two.
///
/// Librarian and Hindsight both subscribe to events.perception and both
/// embed PromptCap.Apply(perception.text) — the same string, so the same
/// vector. Neither knows the other exists and neither should: this sits
/// under both of them, in the provider, so the fix costs no bus message and
/// no ordering assumption. Nothing about the agents changes.
///
/// The cost is worse than a duplicated cheap call, which is why this is
/// worth a type. OnnxEmbeddingProvider holds a lock across inference because
/// an ONNX session is not safe to call concurrently, so the two embeds do
/// not overlap: the second waits for the first to finish and then recomputes
/// a bit-identical answer. Two serial model passes on the critical path
/// where one would do. On the API provider it is two HTTP round trips.
///
/// Capacity is small on purpose. This is a within-turn deduplicator, not a
/// memoiser of the conversation: the entries it is built for arrive
/// milliseconds apart, and holding vectors for old turns would trade a real
/// bound for a speculative hit rate.
/// </summary>
public sealed class CachingEmbeddingProvider(
    IEmbeddingProvider inner,
    ILogger<CachingEmbeddingProvider> logger) : IEmbeddingProvider
{
    private const int Capacity = 8;

    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool Available => inner.Available;

    public string ModelId => inner.ModelId;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (!Available || texts.Count == 0)
        {
            return [];
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var missing = texts.Where(t => !_cache.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                var fresh = await inner.EmbedAsync(missing, cancellationToken).ConfigureAwait(false);

                // A provider that returns nothing is unavailable mid-flight
                // rather than at startup. Say nothing, cache nothing, and let
                // the caller take its no-vector path — the same shape every
                // other Available check has.
                if (fresh.Count != missing.Count)
                {
                    return fresh;
                }

                for (var i = 0; i < missing.Count; i++)
                {
                    Store(missing[i], fresh[i]);
                }
            }
            else
            {
                logger.LogDebug("Embedding cache hit for all {Count} text(s), no model pass", texts.Count);
            }

            // Copied per caller: before this type every EmbedAsync allocated a
            // fresh array, and handing out the cached instance would quietly
            // alias two agents' vectors to one buffer.
            return [.. texts.Select(t => _cache[t].AsSpan().ToArray())];
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Store(string text, float[] vector)
    {
        _cache[text] = vector;
        _order.Enqueue(text);

        while (_order.Count > Capacity)
        {
            _cache.Remove(_order.Dequeue());
        }
    }
}
