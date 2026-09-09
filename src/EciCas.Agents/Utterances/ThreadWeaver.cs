using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Threading: which running subject a new utterance belongs to.
///
/// The log has no addresses, so "my new car is a Tesla" accumulates beside
/// the 2016 row that named the Subaru, and forty restatements of one fact
/// become forty rows competing for the same five slots. A thread id, minted
/// on write, is what makes repetition cost one slot instead of forty and
/// what makes "what car do I drive" answerable as the newest row of a
/// thread -- the one thing the pair store was genuinely good at, recovered
/// without reintroducing a schema to do it.
///
/// **Against the representative, never against any member.** Chaining -- A
/// matches B, B matches C, A does not match C -- walks a thread across
/// subjects over twenty years, and bounded drift matters more here than
/// anywhere given what the archive is promised for. Batch 23 swept all three
/// candidates: <c>first</c> (the frozen opening row) beat <c>centroid</c> at
/// every threshold, and <c>newest</c> is the chaining collapse the roadmap
/// warned about, arriving on schedule.
///
/// **The sweep gates, it does not decide.** Candidates above the line go to
/// the consolidator, five of them, and precision is its problem. What this
/// class settles without a call is the case where cosine clears the line and
/// the content words agree exactly: a verbatim restatement, nothing in
/// dispute, and -- in a chatty archive -- most of the volume. That is where
/// the saving actually comes from.
///
/// **With no consolidator, a candidate splits rather than merges.** The
/// errors are not symmetric: a false split restores today's behaviour, which
/// is duplicates and recoverable at read time; a false merge glues two
/// subjects together and makes *now* wrong, which is not. So a tier without
/// the call gets deterministic dedup of restatements and honest duplication
/// of everything else, and a backfill consolidates it later over an
/// untouched log.
/// </summary>
public sealed class ThreadWeaver
{
    private readonly IUtteranceLog _log;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IUtteranceConsolidator _consolidator;
    private readonly UtteranceOptions _options;
    private readonly ILogger<ThreadWeaver> _logger;

    public ThreadWeaver(IUtteranceLog log, IEmbeddingProvider embeddings, IUtteranceConsolidator consolidator,
        IOptions<UtteranceOptions> options, ILogger<ThreadWeaver> logger)
    {
        _log = log;
        _embeddings = embeddings;
        _consolidator = consolidator;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Embeds, threads and files the rows a turn produced.
    ///
    /// Off the critical path by construction -- the caller has already
    /// answered -- so this is allowed to be the slowest thing in the turn.
    /// Nothing downstream waits on it, and a row that arrives unthreaded is
    /// merely a row the backfill will get to.
    /// </summary>
    public async Task<WeaveResult> WeaveAsync(IReadOnlyList<Utterance> incoming, CancellationToken cancellationToken)
    {
        if (incoming.Count == 0)
        {
            return new WeaveResult(incoming, []);
        }

        if (!_embeddings.Available)
        {
            // No vectors, no sweep. The rows are still ground truth and still
            // worth keeping; ArchiveBackfill's own reasoning applies, which
            // is that a vector is derived and a missing one is a job, not a
            // loss.
            return new WeaveResult(incoming, []);
        }

        var vectors = await _embeddings.EmbedAsync([.. incoming.Select(u => u.Text)], EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
        var corpus = await _log.AllAsync(cancellationToken).ConfigureAwait(false);
        var representatives = Representatives(corpus);
        var threaded = new List<Utterance>(incoming.Count);
        var retired = new List<UtteranceDerived>();

        for (var i = 0; i < incoming.Count; i++)
        {
            var row = incoming[i] with
            {
                Embedding = vectors[i],
                EmbeddingModelId = _embeddings.ModelId,
                FirstSeenTurn = _log.TurnsRecorded,
            };

            var candidates = Candidates(row, representatives);
            var verdict = await ResolveAsync(row, candidates, cancellationToken).ConfigureAwait(false);
            var threadId = verdict?.ThreadId;
            row = row with { ThreadId = threadId ?? row.Id };
            threaded.Add(row);

            // Supersession points backwards: the row the consolidator named
            // is retired *by this one*, so it is the old row that gets
            // rewritten and the new row that stays clean. The other direction
            // would need the newcomer's id before it is filed.
            if (verdict?.Supersedes is { Length: > 0 } stale)
            {
                retired.Add(new UtteranceDerived(stale, SupersededBy: row.Id));
            }

            // A row that minted a thread is itself that thread's frozen
            // representative from here on, and a row that joined one changes
            // nothing about it. Either way the set the next row sweeps has to
            // include it, or two utterances arriving in the same turn cannot
            // find each other.
            if (threadId is null)
            {
                representatives.Add(row);
            }
        }

        return new WeaveResult(threaded, retired);
    }

    /// <summary>
    /// One row per thread: its earliest member, which is what <c>first</c>
    /// means and why it does not drift. Rebuilt per call rather than cached,
    /// because it is a scan of a list already in memory and a stale
    /// representative set is a silently wrong merge.
    /// </summary>
    private static List<Utterance> Representatives(IReadOnlyList<Utterance> corpus)
    {
        var byThread = new Dictionary<string, Utterance>(StringComparer.Ordinal);
        foreach (var row in corpus)
        {
            if (row.ThreadId is null || row.Embedding is null)
            {
                continue;
            }

            if (!byThread.TryGetValue(row.ThreadId, out var held) || row.Timestamp < held.Timestamp)
            {
                byThread[row.ThreadId] = row;
            }
        }

        return [.. byThread.Values];
    }

    private List<(Utterance Row, double Score)> Candidates(Utterance row, List<Utterance> representatives)
    {
        var hits = new List<(Utterance, double)>();
        foreach (var rep in representatives)
        {
            if (rep.Embedding is null || !rep.HasVector(_embeddings.ModelId))
            {
                continue;
            }

            var score = VectorMath.Cosine(row.Embedding!, rep.Embedding);
            if (score >= _options.ThreadThreshold)
            {
                hits.Add((rep, score));
            }
        }

        return [.. hits.OrderByDescending(h => h.Item2).Take(Math.Max(_options.ConsolidatorCandidates, 1))];
    }

    /// <summary>
    /// Which thread, or null to mint a new one.
    ///
    /// Cardinality is the wrong gate and disagreement is the right one. A
    /// lone candidate is not safe to auto-link -- "my wife's new car is a
    /// Volvo" against "my new car is a Tesla" is plausibly a single hit above
    /// the line, and joining it is exactly the unrecoverable error. What
    /// predicts danger is whether the content words disagree, and that is
    /// free to check.
    /// </summary>
    private async Task<ConsolidatorVerdict?> ResolveAsync(Utterance row, List<(Utterance Row, double Score)> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var words = new HashSet<string>(row.Keywords, StringComparer.Ordinal);
        foreach (var (candidate, _) in candidates)
        {
            if (words.Count > 0 && words.SetEquals(candidate.Keywords))
            {
                // A verbatim restatement supersedes nothing: it says what the
                // thread already says, so retiring the older row would lose a
                // first-said date to gain nothing.
                return new ConsolidatorVerdict(candidate.ThreadId, null);
            }
        }

        if (!_options.ConsolidatorEnabled)
        {
            return null;
        }

        try
        {
            return await _consolidator.AdjudicateAsync(row, [.. candidates.Select(c => c.Row)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A consolidator that failed is a consolidator that is not there,
            // and the answer to that is the same as for a tier that never had
            // one: split. Loud, because a substrate down for a week is a week
            // of an archive that duplicates.
            _logger.LogWarning(ex, "Consolidator failed; minting a new thread rather than merging.");
            return null;
        }
    }
}

/// <summary>
/// What a weave produced: the rows to append, and the older rows a verdict
/// retired. Two lists rather than one because they land in different places
/// -- an append and a derived-column update -- and because ground truth is
/// never rewritten, only the disposable columns beside it.
/// </summary>
public sealed record WeaveResult(IReadOnlyList<Utterance> Rows, IReadOnlyList<UtteranceDerived> Retired);
