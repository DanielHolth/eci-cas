using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Gives every utterance the derived columns it is missing: a vector under
/// the embedder currently configured, and a thread.
///
/// The inversion's whole claim is that ground truth is the sentence and
/// everything beside it is disposable. This is the other half of that claim
/// -- disposable only means disposable if something rebuilds it. A turn taken
/// while the embedder was down, a model swapped under a warm archive, a
/// backlog imported from somewhere else: all of them land as rows that are
/// perfectly good ground truth and invisible to a cosine sweep.
///
/// **At boot, beside the pair archive's own backfill, for the same reason.**
/// A deployment that needs an operator to remember a maintenance command is a
/// deployment where the vectors are off and nothing says so. It is free on a
/// warm log -- one pass over a list already in memory, no writes.
///
/// **It threads without a consolidator, always.** A boot job is the wrong
/// place to spend a substrate call per row: a thousand-row backlog would be a
/// thousand calls before the surface answers. So it applies the free half of
/// the write-time rule -- above the line *and* the content words agree exactly
/// -- and leaves everything else in its own thread, which is the same
/// degradation a tier without a consolidator already lives with, and
/// recoverable the same way.
/// </summary>
public sealed class UtteranceBackfill
{
    private readonly IUtteranceLog _log;
    private readonly IEmbeddingProvider _embeddings;
    private readonly UtteranceOptions _options;

    public UtteranceBackfill(IUtteranceLog log, IEmbeddingProvider embeddings, IOptions<UtteranceOptions> options)
    {
        _log = log;
        _embeddings = embeddings;
        _options = options.Value;
    }

    /// <summary>
    /// Rows given a vector, and rows given a thread. Both zero is the
    /// ordinary case.
    /// </summary>
    public async Task<(int Embedded, int Threaded)> RunAsync(CancellationToken cancellationToken)
    {
        if (!_embeddings.Available)
        {
            return (0, 0);
        }

        var corpus = await _log.AllAsync(cancellationToken).ConfigureAwait(false);
        if (corpus.Count == 0)
        {
            return (0, 0);
        }

        var modelId = _embeddings.ModelId;
        var bare = corpus.Where(r => !r.HasVector(modelId)).ToList();
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);

        if (bare.Count > 0)
        {
            var embedded = await _embeddings
                .EmbedAsync([.. bare.Select(r => r.Text)], EmbeddingKind.Passage, cancellationToken)
                .ConfigureAwait(false);

            // Short of a vector each, the log would be left half-stamped for
            // no gain. Leaving it exactly as it was is a job for the next
            // boot; a partial one is a job nobody can describe.
            if (embedded.Count != bare.Count)
            {
                return (0, 0);
            }

            for (var i = 0; i < bare.Count; i++)
            {
                vectors[bare[i].Id] = embedded[i];
            }
        }

        // Work against the corpus as it will be once the vectors land, so a
        // row embedded in this pass can still be threaded in it.
        var current = corpus
            .Select(r => vectors.TryGetValue(r.Id, out var v)
                ? r with { Embedding = v, EmbeddingModelId = modelId }
                : r)
            .OrderBy(r => r.Timestamp)
            .ToList();

        // Earliest member per thread: the same frozen representative the
        // write path threads against, so a backfilled row joins what a live
        // one would have joined.
        var representatives = new Dictionary<string, Utterance>(StringComparer.Ordinal);
        foreach (var row in current.Where(r => r.ThreadId is not null && r.Embedding is not null))
        {
            if (!representatives.TryGetValue(row.ThreadId!, out var held) || row.Timestamp < held.Timestamp)
            {
                representatives[row.ThreadId!] = row;
            }
        }

        var updates = new List<UtteranceDerived>();
        var threaded = 0;

        foreach (var row in current)
        {
            var gainedVector = vectors.ContainsKey(row.Id);
            if (row.ThreadId is not null)
            {
                if (gainedVector)
                {
                    updates.Add(new UtteranceDerived(row.Id, row.Embedding, modelId));
                }

                continue;
            }

            var thread = Join(row, representatives.Values) ?? row.Id;
            updates.Add(new UtteranceDerived(row.Id, gainedVector ? row.Embedding : null,
                gainedVector ? modelId : null, thread));
            threaded++;

            var settled = row with { ThreadId = thread };
            if (!representatives.ContainsKey(thread))
            {
                representatives[thread] = settled;
            }
        }

        if (updates.Count > 0)
        {
            await _log.UpdateDerivedAsync(updates, cancellationToken).ConfigureAwait(false);
        }

        return (vectors.Count, threaded);
    }

    /// <summary>
    /// The free half of the write-time rule: above the line and the content
    /// words agree exactly. Anything short of that keeps its own thread --
    /// see the class remarks for why a boot job does not adjudicate.
    /// </summary>
    private string? Join(Utterance row, IEnumerable<Utterance> representatives)
    {
        if (row.Embedding is null || row.Keywords.Count == 0)
        {
            return null;
        }

        var words = new HashSet<string>(row.Keywords, StringComparer.Ordinal);
        string? best = null;
        var bestScore = _options.ThreadThreshold;

        foreach (var rep in representatives)
        {
            if (rep.Embedding is null || rep.ThreadId is null || !words.SetEquals(rep.Keywords))
            {
                continue;
            }

            var score = VectorMath.Cosine(row.Embedding, rep.Embedding);
            if (score >= bestScore)
            {
                bestScore = score;
                best = rep.ThreadId;
            }
        }

        return best;
    }
}
