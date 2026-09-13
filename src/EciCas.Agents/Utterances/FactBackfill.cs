using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Rebuilds the index from what was said.
///
/// The inversion's claim is that ground truth is the sentence and everything
/// beside it is disposable. This is the other half of that claim -- disposable
/// only means disposable if something rebuilds it. A turn taken while the
/// extractor was down, a turn taken before the extractor existed, an embedder
/// swapped under a warm archive, a backlog imported from somewhere else: all
/// of them are perfectly good ground truth that no cosine sweep can see.
///
/// **Two modes, and the difference is who asked.**
/// <see cref="RunAsync"/> fills gaps: utterances with no facts, facts with no
/// vector, facts with no thread. It runs at boot, because a deployment that
/// needs an operator to remember a maintenance command is a deployment where
/// the index is stale and nothing says so. It is free on a warm archive --
/// one pass over a list already in memory, no writes.
/// <see cref="RebuildAsync"/> throws the whole index away and reads every
/// utterance again. That is what a better extractor, a better prompt, or a
/// suspected corruption is worth, and it is only ever run by hand.
/// <see cref="RedriveAsync"/> is the same idea aimed: it re-reads only the
/// turns whose facts were written by a model it is allowed to replace, with
/// a stronger extractor passed in. It runs at boot like the gap-fill, and it
/// converges, because the rows it writes name a model it may not replace.
///
/// **It threads without a consolidator, always.** A boot job is the wrong
/// place to spend a substrate call per row: a thousand-row backlog would be a
/// thousand calls before the surface answers. So it applies the free half of
/// the write-time rule -- above the line *and* the content words agree exactly
/// -- and leaves everything else in its own thread, which is the same
/// degradation a tier without a consolidator already lives with.
/// </summary>
public sealed class FactBackfill
{
    private readonly IUtteranceLog _utterances;
    private readonly IFactLog _facts;
    private readonly IFactExtractor _extractor;
    private readonly IEmbeddingProvider _embeddings;
    private readonly UtteranceOptions _options;
    private readonly ILogger<FactBackfill> _logger;

    public FactBackfill(IUtteranceLog utterances, IFactLog facts, IFactExtractor extractor,
        IEmbeddingProvider embeddings, IOptions<UtteranceOptions> options, ILogger<FactBackfill> logger)
    {
        _utterances = utterances;
        _facts = facts;
        _extractor = extractor;
        _embeddings = embeddings;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Utterances read, rows given a vector, rows given a thread. All three
    /// zero is the ordinary case.
    /// </summary>
    public sealed record Result(int Extracted, int Embedded, int Threaded);

    /// <summary>What a re-derivation touched, and why it stopped if it did.</summary>
    public sealed record Redrive(int Turns, int Rows, string? Stopped);

    /// <summary>
    /// Reads the turns behind weak facts again, with a better extractor.
    ///
    /// **Nothing is deleted before its replacement exists.** The model call
    /// comes first, the old rows come out and the new ones go in afterwards,
    /// and a turn whose call failed is left exactly as it was. That ordering
    /// is the whole safety argument: <see cref="IFactExtractor"/> is
    /// contractually forbidden to throw and degrades to the utterance
    /// verbatim instead, so a rebuild that deleted first would answer a dead
    /// network by replacing good facts with paragraphs.
    ///
    /// **A failure stops the pass rather than skipping the turn.** A verbatim
    /// row is how a failed call announces itself -- no model answered, so no
    /// model is named -- and one of those means the substrate is gone, not
    /// that this one sentence was hard. Everything already rewritten stands;
    /// the next boot picks up the rest, because the rows it has not reached
    /// still name a model it is allowed to replace.
    ///
    /// **Written per turn, not in one batch at the end.** It costs a store
    /// rewrite per turn against a store that is rewritten constantly anyway,
    /// and it buys the thing that actually matters here: a rebuild killed
    /// halfway keeps the calls it already paid for.
    /// </summary>
    public async Task<Redrive> RedriveAsync(IFactExtractor extractor, IReadOnlyCollection<string> replaces,
        CancellationToken cancellationToken)
    {
        var owed = (await _facts.AllAsync(cancellationToken).ConfigureAwait(false))
            .Where(f => f.WrittenBy(replaces))
            .GroupBy(f => f.Turn)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToList());

        if (owed.Count == 0)
        {
            return new Redrive(0, 0, null);
        }

        var utterances = await _utterances.AllAsync(cancellationToken).ConfigureAwait(false);
        var replies = await _utterances.RepliesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Re-reading {Count} turn(s) whose facts are owed a better extractor.", owed.Count);

        var turns = 0;
        var written = 0;
        foreach (var utterance in utterances.Where(u => owed.ContainsKey(u.Turn)).OrderBy(u => u.Turn))
        {
            var previous = UtteranceContext.PreviousReply(replies, utterance);
            var extracted = await extractor.ExtractAsync(utterance, previous, cancellationToken).ConfigureAwait(false);

            // An unnamed model is a failed call, not a verdict. NONE comes
            // back as an empty list and is a verdict -- a stronger reader
            // saying this turn stated nothing -- so the weak rows go.
            if (extracted.Any(e => e.OriginModel is null))
            {
                return new Redrive(turns, written,
                    $"the substrate stopped answering at turn {utterance.Turn}; the rest is left for the next boot");
            }

            var rows = FactMint.Rows(utterance, extracted, _options);
            await _facts.RemoveAsync(owed[utterance.Turn], cancellationToken).ConfigureAwait(false);
            await _facts.AppendAsync(rows, cancellationToken).ConfigureAwait(false);
            turns++;
            written += rows.Count;
        }

        await DeriveAsync(cancellationToken).ConfigureAwait(false);
        return new Redrive(turns, written, null);
    }

    /// <summary>Gap-filling only. Safe to run on every boot.</summary>
    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        var facts = await _facts.AllAsync(cancellationToken).ConfigureAwait(false);
        var indexed = facts.Select(f => f.Turn).ToHashSet();
        var utterances = await _utterances.AllAsync(cancellationToken).ConfigureAwait(false);

        var extracted = await ExtractAsync([.. utterances.Where(u => !indexed.Contains(u.Turn))], cancellationToken)
            .ConfigureAwait(false);
        var (embedded, threaded) = await DeriveAsync(cancellationToken).ConfigureAwait(false);
        return new Result(extracted, embedded, threaded);
    }

    /// <summary>
    /// Everything again, from zero. Destroys nothing that was not computed:
    /// the utterance log is opened read-only here and is not touched by any
    /// path in this class.
    /// </summary>
    public async Task<Result> RebuildAsync(CancellationToken cancellationToken)
    {
        var utterances = await _utterances.AllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Rebuilding the fact store from {Count} utterances.", utterances.Count);

        await _facts.ClearAsync(cancellationToken).ConfigureAwait(false);
        var extracted = await ExtractAsync(utterances, cancellationToken).ConfigureAwait(false);
        var (embedded, threaded) = await DeriveAsync(cancellationToken).ConfigureAwait(false);
        return new Result(extracted, embedded, threaded);
    }

    /// <summary>
    /// Reads facts out of utterances and files them bare -- no vector, no
    /// thread. <see cref="DeriveAsync"/> picks them up in the same pass,
    /// which is also how a row that arrived unembedded during a live turn
    /// gets settled; there is one threading implementation, not two.
    /// </summary>
    private async Task<int> ExtractAsync(IReadOnlyList<Utterance> utterances, CancellationToken cancellationToken)
    {
        if (utterances.Count == 0)
        {
            return 0;
        }

        var replies = await _utterances.RepliesAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<Fact>();
        foreach (var utterance in utterances)
        {
            var previous = UtteranceContext.PreviousReply(replies, utterance);
            rows.AddRange(FactMint.Rows(utterance, await _extractor.ExtractAsync(utterance, previous, cancellationToken).ConfigureAwait(false), _options));
        }

        await _facts.AppendAsync(rows, cancellationToken).ConfigureAwait(false);
        return utterances.Count;
    }


    /// <summary>
    /// Gives every fact the derived columns it is missing: a vector under the
    /// embedder currently configured, and a thread.
    /// </summary>
    private async Task<(int Embedded, int Threaded)> DeriveAsync(CancellationToken cancellationToken)
    {
        if (!_embeddings.Available)
        {
            return (0, 0);
        }

        // Markers have no sentence to embed and nothing to thread against.
        var corpus = (await _facts.AllAsync(cancellationToken).ConfigureAwait(false)).Where(r => !r.IsMarker).ToList();
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

            // Short of a vector each, the store would be left half-stamped for
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
        var representatives = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var row in current.Where(r => r.ThreadId is not null && r.Embedding is not null))
        {
            if (!representatives.TryGetValue(row.ThreadId!, out var held) || row.Timestamp < held.Timestamp)
            {
                representatives[row.ThreadId!] = row;
            }
        }

        var updates = new List<FactDerived>();
        var threaded = 0;

        foreach (var row in current)
        {
            var gainedVector = vectors.ContainsKey(row.Id);
            if (row.ThreadId is not null)
            {
                if (gainedVector)
                {
                    updates.Add(new FactDerived(row.Id, row.Embedding, modelId));
                }

                continue;
            }

            var thread = Join(row, representatives.Values) ?? row.Id;
            updates.Add(new FactDerived(row.Id, gainedVector ? row.Embedding : null,
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
            await _facts.UpdateDerivedAsync(updates, cancellationToken).ConfigureAwait(false);
        }

        return (vectors.Count, threaded);
    }

    /// <summary>
    /// The free half of the write-time rule: above the line and the content
    /// words agree exactly. Anything short of that keeps its own thread --
    /// see the class remarks for why a boot job does not adjudicate.
    /// </summary>
    private string? Join(Fact row, IEnumerable<Fact> representatives)
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
