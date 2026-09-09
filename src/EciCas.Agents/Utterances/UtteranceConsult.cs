using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>One row chosen for a read, with what it scored and why it is here.</summary>
public sealed record Consulted(Utterance Row, double Score, bool Current);

/// <summary>
/// Find: the read that means *now*.
///
/// A full-corpus cosine sweep, fused with a lexical lane, collapsed by
/// thread, and diversified. No routing, no shelf, no chunk-and-pick -- every
/// row is a candidate on every read, which is the whole point of the
/// inversion and is affordable because the corpus is already in memory and
/// the vectors are already normalised.
///
/// **Collapse by thread, then take the newest member.** Not the nearest.
/// The nearest member of a thread is usually the oldest phrasing of it --
/// the one the query most resembles, because it is the one that stated the
/// fact plainly -- and answering "what car do I drive" with the row that
/// says Subaru because it matches better is exactly the failure the thread
/// exists to prevent. So a thread earns its slot on its best member's score
/// and then spends it on its newest.
///
/// **Two reads, not one.** Pass A drops superseded rows and is the answer to
/// what is true; pass B is unrestricted and tops up the slots A could not
/// fill. A read that only ever saw pass A would make the archive amnesiac
/// about its own history; a read that never separated them would let a
/// retired fact compete with the one that replaced it.
///
/// **MMR, not jitter.** Five slots holding one fact five times is five
/// wasted slots. Diversity at lambda 0.7 bought +0.14 distinct facts per
/// read; score jitter, tried for the same purpose on the same bench, lost
/// 0.11. Diversity here is chosen, not randomised.
/// </summary>
public sealed class UtteranceConsult
{
    private readonly IUtteranceLog _log;
    private readonly IEmbeddingProvider _embeddings;
    private readonly UtteranceOptions _options;

    public UtteranceConsult(IUtteranceLog log, IEmbeddingProvider embeddings, IOptions<UtteranceOptions> options)
    {
        _log = log;
        _embeddings = embeddings;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<Consulted>> FindAsync(string query, CancellationToken cancellationToken)
    {
        var corpus = await _log.AllAsync(cancellationToken).ConfigureAwait(false);
        if (corpus.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        float[]? queryVector = null;
        if (_embeddings.Available)
        {
            var embedded = await _embeddings.EmbedAsync([query], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            queryVector = embedded[0];
        }

        // Rarity is a property of the corpus as it stands, so it is computed
        // here rather than frozen into the rows -- see KeywordExtractor for
        // why the stored set is the corpus-independent one.
        var df = KeywordExtractor.DocumentFrequency(corpus.Select(r => r.Keywords));
        var rare = new HashSet<string>(KeywordExtractor.Rare(query, df, _options.RareMax), StringComparer.Ordinal);

        var turnsNow = _log.TurnsRecorded;
        var scored = new List<(Utterance Row, double Score)>();
        foreach (var row in corpus)
        {
            var cosine = queryVector is not null && row.Embedding is not null && row.HasVector(_embeddings.ModelId)
                ? VectorMath.Cosine(queryVector, row.Embedding)
                : 0.0;
            var lexical = Lexical(rare, row);
            var score = ((1 - _options.LexicalWeight) * cosine) + (_options.LexicalWeight * lexical);
            if (score >= _options.ReadMinScore)
            {
                scored.Add((row, score));
            }
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var current = Collapse(scored.Where(s => s.Row.SupersededBy is null), corpus, turnsNow, true);
        var picked = Select(current, []);

        if (picked.Count < _options.TopK)
        {
            var all = Collapse(scored, corpus, turnsNow, false);
            picked.AddRange(Select(all, [.. picked.Select(p => p.Row.ThreadId ?? p.Row.Id)])
                .Take(_options.TopK - picked.Count));
        }

        await _log.RecordHitsAsync([.. picked.Select(p => p.Row.Id)], cancellationToken).ConfigureAwait(false);
        return picked;
    }

    /// <summary>
    /// Fraction of the query's rare words the row actually contains. Zero
    /// when the query has none, which is most queries -- the lexical lane is
    /// a rescue for token questions, not a second opinion on every read.
    /// </summary>
    private static double Lexical(HashSet<string> rare, Utterance row)
    {
        if (rare.Count == 0)
        {
            return 0;
        }

        var hits = row.Keywords.Count(rare.Contains);
        return hits / (double)rare.Count;
    }

    /// <summary>
    /// One entry per thread: the thread's best score, the thread's newest
    /// member. Hit rate breaks ties *within* a thread only -- across threads
    /// it would promote whatever the archive is already in the habit of
    /// saying, which is how a memory gets stuck.
    /// </summary>
    private static List<Consulted> Collapse(IEnumerable<(Utterance Row, double Score)> scored, IReadOnlyList<Utterance> corpus, long turnsNow, bool current)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (row, score) in scored)
        {
            var thread = row.ThreadId ?? row.Id;
            if (!best.TryGetValue(thread, out var held) || score > held)
            {
                best[thread] = score;
            }
        }

        var newest = new Dictionary<string, Utterance>(StringComparer.Ordinal);
        foreach (var row in corpus)
        {
            var thread = row.ThreadId ?? row.Id;
            if (!best.ContainsKey(thread))
            {
                continue;
            }

            if (current && row.SupersededBy is not null)
            {
                continue;
            }

            if (!newest.TryGetValue(thread, out var held)
                || row.Timestamp > held.Timestamp
                || (row.Timestamp == held.Timestamp && row.HitRate(turnsNow) > held.HitRate(turnsNow)))
            {
                newest[thread] = row;
            }
        }

        return [.. newest.Select(kv => new Consulted(kv.Value, best[kv.Key], current))
            .OrderByDescending(c => c.Score)];
    }

    /// <summary>
    /// Maximal marginal relevance over the collapsed threads, penalising a
    /// candidate by how much it resembles what is already chosen.
    /// </summary>
    private List<Consulted> Select(List<Consulted> candidates, IReadOnlyList<string> alreadyThreaded)
    {
        var taken = new HashSet<string>(alreadyThreaded, StringComparer.Ordinal);
        var chosen = new List<Consulted>();
        var pool = candidates.Where(c => !taken.Contains(c.Row.ThreadId ?? c.Row.Id)).ToList();

        while (chosen.Count < _options.TopK && pool.Count > 0)
        {
            var bestIndex = 0;
            var bestValue = double.NegativeInfinity;
            for (var i = 0; i < pool.Count; i++)
            {
                var redundancy = chosen.Count == 0 ? 0 : chosen.Max(c => Similarity(pool[i].Row, c.Row));
                var value = (_options.DiversityLambda * pool[i].Score) - ((1 - _options.DiversityLambda) * redundancy);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = i;
                }
            }

            chosen.Add(pool[bestIndex]);
            pool.RemoveAt(bestIndex);
        }

        return chosen;
    }

    /// <summary>
    /// Cosine where both rows have a vector, keyword overlap where they do
    /// not. The fallback matters: an unembedded backlog would otherwise look
    /// maximally distinct from everything and crowd the slots it is least
    /// entitled to.
    /// </summary>
    private double Similarity(Utterance a, Utterance b)
    {
        if (a.Embedding is not null && b.Embedding is not null
            && a.HasVector(_embeddings.ModelId) && b.HasVector(_embeddings.ModelId))
        {
            return VectorMath.Cosine(a.Embedding, b.Embedding);
        }

        var left = new HashSet<string>(a.Keywords, StringComparer.Ordinal);
        if (left.Count == 0 || b.Keywords.Count == 0)
        {
            return 0;
        }

        var shared = b.Keywords.Count(left.Contains);
        return shared / (double)Math.Max(left.Count, b.Keywords.Count);
    }
}
