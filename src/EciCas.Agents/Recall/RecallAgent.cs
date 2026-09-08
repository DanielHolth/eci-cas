using EciCas.Agents.Perception;
using EciCas.Agents.Librarian;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace EciCas.Agents.Recall;

/// <summary>
/// Second stage of the knowledge swarm: for each (Category, Topic) pair
/// Librarian selected, reads that pair's rows once, splits them into chunks
/// of RecallOptions.RowsPerWorker, and fires one substrate call per chunk
/// asking which rows are actually relevant. Subtopic resolution happens
/// here, in the picking model's reading of the rows, rather than upstream in
/// Librarian's index.
///
/// A pair is never truncated: a subtopic discussed at great length simply
/// produces more chunks. That trades a per-pair row cap for a per-turn call
/// cap (MaxConcurrentRecalls), which loses nothing from a shallow archive
/// and degrades gracefully on a deep one.
///
/// Every worker across every pair is built before a single substrate call
/// starts, then awaited in one flat Task.WhenAll — never a chain where a
/// second wave is discovered only after the first returns. Turn latency is
/// therefore one file read plus one substrate call, not N of either.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: N parallel substrate calls per envelope doesn't
/// fit that base class's one-call model — same rationale ArchivistAgent's
/// own doc comment gives for its choice.
/// </summary>
public sealed class RecallAgent : AgentBase, ICognitiveAgent
{
    public const string RecalledFactsKey = "recall.facts";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly RecallOptions _options;
    private readonly RuntimeKnobs _knobs;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ILogger _logger;

    public RecallAgent(IMessageBus bus, BusActivityTracker activity, ILogger<RecallAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<RecallOptions> options,
        IInstructionStore instructions, RuntimeKnobs knobs, IEmbeddingProvider embeddings)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _knobs = knobs;
        _embeddings = embeddings;
        _logger = logger;
    }

    public override string Name => "Recall";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.SelectedPairs];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var pairs = envelope.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey) ?? [];

        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));
        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);

        // The recency lane, read whatever Librarian selected and even when it
        // selected nothing. It is not a pair and is not in the index: it is
        // the newest rows written anywhere, and the questions it answers
        // ("what did I say about that", "what has been going on") name no
        // drawer, so no selector can reach them.
        var recent = await _store.RecentAsync(profileId, _options.RecentRows, cancellationToken).ConfigureAwait(false);

        // No substrate by configuration: nothing can be picked, so the lane
        // is handed over as it stands, newest first and cut to the same depth
        // a picking call would have returned. A tier without a picking model
        // still knows what was said lately.
        if (!entry.UseSubstrate)
        {
            Publish(envelope, Distinct(recent.Take(_knobs.RecallDepth)), degraded: null);
            return;
        }

        // Phase one: read every selected pair at once. Distinct pairs are
        // distinct files, so these don't contend with each other.
        var read = await Task.WhenAll(pairs.Select(p => _store.LookupAsync(p, profileId, cancellationToken))).ConfigureAwait(false);

        // The cosine cut, before anything is chunked. This is where the row
        // vectors earn their disk: a set every row of which carries a current
        // vector is narrowed to the handful nearest the question, and what
        // reaches the workers below is already the right end of the file
        // rather than the first RowsPerWorker of it in importance order.
        //
        // One embed for the turn, and the same cut over both kinds of
        // candidate. The lane used to reach Intent on timestamp alone -- the
        // newest RecallDepth rows, whatever the turn was about -- which is
        // how a question about a daughter arrived carrying four rows of the
        // persona's own identity, written twenty minutes earlier. Recency is
        // how the lane is built, not a reason to hand Intent its head: once a
        // row is a candidate it is ranked like every other candidate, and the
        // lane keeps only what it is for, which is reaching rows no selector
        // can name.
        var query = await QueryVectorAsync(envelope, text, cancellationToken).ConfigureAwait(false);
        var (loaded, narrowedPairs) = Narrow(query, read);
        var (lane, narrowedLane) = Narrow(query, [recent], "recent");
        recent = lane[0];
        var narrowedAll = narrowedPairs && narrowedLane;

        // When every loaded row would fit in a single worker's pick budget,
        // the picking call can only return a subset of what passing them all
        // gives Intent. Skipping it removes one of the turn's serial
        // substrate calls on a young archive. Order is preserved — the store
        // already sorted by Importance.
        //
        // Librarian used to skip on the same reasoning and no longer does:
        // its cap bounds how many pairs Recall opens, so passing an
        // under-cap index whole is only free while the index is tiny, and it
        // meant the selector never ran during exactly the period its
        // judgment was being tuned. This one stays because the budget it
        // guards is per-worker, not per-turn — an under-budget chunk is
        // genuinely nothing to choose from.
        // Guarded because the join is an argument, not part of the template:
        // it walks every loaded row on every turn at every level, and only
        // Debug ever reads the result.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} read {Rows} row(s) from {Pairs}: {Loaded}",
                Name, loaded.Sum(rows => rows.Count), pairs.Count,
                string.Join(" | ", loaded.SelectMany(rows => rows).Select(Describe)));
        }

        // Ranking is not picking, and it used to be the whole of the read:
        // cosine puts the fact in the top five 97% of the time given the
        // right file, so a cut that kept fewer rows than a worker could
        // return made the picking call pure subtraction, and the bench
        // measured it subtracting the right answer.
        //
        // So the cut is the depth itself and the two stages no longer
        // overlap: a set holding no more rows than a worker may return has
        // nothing left to pick from, and PickAfterVector is false everywhere
        // because of it. Raising the cut above the depth is what turns the
        // picking call back on -- cosine saying which rows are worth reading,
        // the model saying which of them answer the question.
        if (narrowedAll && !_options.PickAfterVector && loaded.Length > 0)
        {
            _logger.LogInformation("{Agent} vector-narrowed every pair and the lane; skipping the picking calls", Name);
            Publish(envelope, Distinct(loaded.SelectMany(rows => rows).Concat(recent)), degraded: null);
            return;
        }

        var total = loaded.Sum(rows => rows.Count) + recent.Count;
        if (total <= _knobs.RecallDepth)
        {
            Publish(envelope, Distinct(loaded.SelectMany(rows => rows).Concat(recent)), degraded: null);
            return;
        }

        // Phase two: one flat set of workers over every chunk of every pair,
        // plus one for the lane. Appended after the fan-out cap rather than
        // inside it, because the cap is a budget over the selected pairs and
        // the lane is not one of them — a deep archive must not be able to
        // spend the turn and leave the lane unread.
        var chunks = new List<IReadOnlyList<ArchiveRecord>>(Chunks(loaded));
        if (recent.Count > 0)
        {
            chunks.Add(recent);
        }

        var results = await Task.WhenAll(chunks.Select(c => PickAsync(envelope, c, text, entry.Class, cancellationToken))).ConfigureAwait(false);
        var picked = Distinct(results.SelectMany(r => r.Facts));

        // Any worker failing means some of the archive went unread, so the
        // turn is grounded in less than it should have been — one failure is
        // enough to say so, and the first cause is as good as any.
        var degraded = results.Select(r => r.Degraded).FirstOrDefault(c => c is not null);

        // No aggregate line: one line per substrate call, printed by the
        // worker that made it. Folding N calls into a total needed a
        // wall-clock caveat to not be misread as a sum, and the per-call
        // lines carry the same numbers without needing one.
        Publish(envelope, picked, degraded);
    }

    /// <summary>
    /// Narrows each loaded set of rows to the ones nearest this turn, and
    /// says whether every set could be narrowed that way. A set is a pair
    /// Librarian selected or the recency lane -- the lane is not a pair, but
    /// by the time it is a list of candidate rows there is nothing left to
    /// distinguish them, and one cut is easier to reason about than two.
    ///
    /// The rule is all-or-nothing per pair, and deliberately: a file where
    /// half the rows carry a vector would be swept half-blind, and the half
    /// with no vector would lose every time regardless of what it says. So a
    /// pair narrows only when every row in it carries a vector from the
    /// current model over text that still matches what the row says - and
    /// otherwise passes through whole to the chunk-and-pick path, which is
    /// what this system did before vectors existed and still works.
    ///
    /// That makes an archive written before the embedder arrived, or written
    /// while it was down, simply slower rather than wrong. Backfilling is
    /// what turns those pairs on.
    /// </summary>
    private (IReadOnlyList<ArchiveRecord>[] Loaded, bool NarrowedAll) Narrow(
        float[]? query, IReadOnlyList<ArchiveRecord>[] loaded, string? label = null)
    {
        if (query is null || loaded.Length == 0 || _knobs.VectorCandidates <= 0 || !_embeddings.Available)
        {
            return (loaded, false);
        }

        var modelId = _embeddings.ModelId;
        var narrowed = new IReadOnlyList<ArchiveRecord>[loaded.Length];
        var all = true;

        for (var i = 0; i < loaded.Length; i++)
        {
            var rows = loaded[i];
            if (rows.Count == 0 || !rows.All(r => r.HasVector(modelId)))
            {
                narrowed[i] = rows;
                all = all && rows.Count == 0;
                continue;
            }

            narrowed[i] =
            [
                .. rows
                    .Select(r => (Row: r, Score: VectorMath.Cosine(query, r.Embedding!)))
                    .OrderByDescending(x => x.Score)
                    .Take(_knobs.VectorCandidates)
                    .Select(x => x.Row),
            ];

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("{Agent} narrowed {Set} from {Before} to {After} row(s) by cosine",
                    Name, label ?? $"{rows[0].Category}/{rows[0].Topic}", rows.Count, narrowed[i].Count);
            }
        }

        return (narrowed, all);
    }

    /// <summary>
    /// Librarian's vector when it published one, and this turn's own embed
    /// when it did not - a deterministic Librarian, or one whose embed
    /// failed, must not cost Recall its ranking. Null means no ranking, which
    /// every caller above treats as the ordinary pre-vector path.
    /// </summary>
    private async Task<float[]?> QueryVectorAsync(Envelope envelope, string text, CancellationToken cancellationToken)
    {
        if (envelope.Meta.Get<float[]>(LibrarianAgent.QueryVectorKey) is { Length: > 0 } published)
        {
            return published;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var vectors = await _embeddings.EmbedAsync([text], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            return vectors.Count == 0 ? null : vectors[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Agent} could not embed the turn; reading without vector ranking", Name);
            return null;
        }
    }

    /// <summary>
    /// Splits every loaded pair into worker-sized chunks, then trims to
    /// MaxConcurrentRecalls. The trim is round-robin by chunk depth rather
    /// than pair-by-pair: rows are Importance-ordered, so each pair's first
    /// chunk is its most valuable one, and taking breadth-first means a
    /// single deep pair can't spend the whole budget and starve the others.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<ArchiveRecord>> Chunks(IReadOnlyList<IReadOnlyList<ArchiveRecord>> loaded)
    {
        var perPair = loaded
            .Select(rows => rows.Chunk(Math.Max(1, _options.RowsPerWorker)).ToList())
            .ToList();

        var chunks = new List<IReadOnlyList<ArchiveRecord>>();
        var depth = perPair.Count == 0 ? 0 : perPair.Max(p => p.Count);
        for (var d = 0; d < depth; d++)
        {
            foreach (var pair in perPair.Where(p => d < p.Count))
            {
                chunks.Add(pair[d]);
            }
        }

        if (chunks.Count <= _options.MaxConcurrentRecalls)
        {
            return chunks;
        }

        // Said out loud, because the rows in a dropped chunk were selected,
        // loaded, and then never read, and nothing downstream shows it: the
        // turn looks like an ordinary one that recalled less. Not marked
        // degraded — the cap is a deliberate budget, not a substrate
        // failing — but it is the number that explains a thin answer from a
        // deep archive.
        _logger.LogWarning("{Agent} fan-out capped at {Cap}: {Dropped} chunk(s) of loaded rows go unread this turn",
            Name, _options.MaxConcurrentRecalls, chunks.Count - _options.MaxConcurrentRecalls);

        return [.. chunks.Take(_options.MaxConcurrentRecalls)];
    }

    /// <summary>
    /// Importance order, one row per address. The lane holds copies of rows
    /// the pair files also hold, so the same fact can be picked twice in one
    /// turn — by its drawer and by its recency — and Intent should see it
    /// once. The pair is part of the key: the lane spans every drawer, so
    /// subtopic/subject/key alone is not an identity across it.
    /// </summary>
    private static IReadOnlyList<ArchiveRecord> Distinct(IEnumerable<ArchiveRecord> facts) =>
        [.. facts
            .GroupBy(r => (r.Category.ToLowerInvariant(), r.Topic.ToLowerInvariant(),
                r.Subtopic.ToLowerInvariant(), r.Subject.ToLowerInvariant(), r.Key.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderByDescending(r => r.Importance)];

    private void Publish(Envelope envelope, IReadOnlyList<ArchiveRecord> facts, string? degraded)
    {
        // Logged here rather than beside the picking calls, because the two
        // paths that skip those calls — nothing selected, and an archive
        // small enough to pass whole — still hand Intent facts, and a turn
        // that recalled Morrow silently is indistinguishable from one that
        // recalled nothing.
        _logger.LogInformation("{Agent} {Facts}", Name,
            facts.Count == 0 ? "nothing on file" : string.Join(", ", facts.Select(Describe)));

        var meta = MetaBag.Empty.With(RecalledFactsKey, facts);

        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, SubstrateHealth.Mark(meta, degraded));
        _bus.Publish(Topics.Advisories, advisory);
    }

    private static string Describe(ArchiveRecord r) =>
        $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value} (importance {r.Importance})";

    /// <summary>
    /// One substrate call scoped to a single chunk's candidates. Failure is
    /// non-gating and isolated: a broken call contributes nothing for this
    /// chunk, no retry, no turn-level failure. Cost is logged here, one line
    /// per call; the diagnostics still travel back because the degraded flag
    /// is a turn-level decision only HandleAsync can make.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics, string? Degraded)> PickAsync(
        Envelope envelope, IReadOnlyList<ArchiveRecord> candidates, string text, string substrateClass, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return ([], null, null);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var prompt = BuildPrompt(text, candidates);
            _logger.LogDebug("{Agent} picking prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} picking response <<<\n{Response}", Name, result.Text);

            // Named by the pair rather than an ordinal: "worker 2 of 3" is
            // not a fact about the archive, and the failure path below
            // already names the pair — so an ordinal made the success line
            // say less about the same chunk than the warning does.
            var read = candidates[0];
            _logger.LogInformation("{Agent} picked from {Category}/{Topic} ({Rows} row(s)) [{Class}]: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, read.Category, read.Topic, candidates.Count, substrateClass,
                result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            SubstrateTrace.Publish(_bus, envelope, Name, substrateClass, result, $"{read.Category}/{read.Topic}");

            return (ParsePicked(result.Text, candidates), result, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var first = candidates[0];
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} lookup for {Category}/{Topic} {Cause}, skipping", Name, first.Category, first.Topic, cause);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, substrateClass,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, cause, $"{first.Category}/{first.Topic}");
            return ([], null, cause);
        }
    }

    private string BuildPrompt(string text, IReadOnlyList<ArchiveRecord> candidates)
    {
        // Category/Topic withheld — redundant with scope. Subtopic is shown,
        // because it is no longer part of the address: it's now the main
        // signal separating one group of rows from another within a pair.
        // Timestamp/Domain/Importance withheld to keep context lean. Rows are
        // already pre-sorted by Importance by the store, not re-sorted here.
        //
        // The turn's own text is included so picking is relevance-to-THIS-
        // question, not just "important in general" — without it an
        // "assistant" row would look just as pickable for a question about
        // the human's name as a person-category row, since nothing here
        // ranked one over the other. What the categories mean is left to the
        // model: the path segments say it in words it already knows.
        // Rendered, not hand-formatted here: rows carry a plain-sentence
        // restatement when the Archivist wrote one, and this is the stage
        // that stands to gain from it. Batch 12 measured Recall's filtering
        // as an 18pp loss — the largest in the log — against a row form so
        // telegraphic it matches almost nothing a question says.
        var rows = string.Join("\n", candidates.Select((r, i) => $"{i}. {r.Rendered}"));
        return InstructionFile.Fill(_instructions.For(Name),
            ("rows", rows),
            ("max", _knobs.RecallDepth.ToString()),
            ("text", text));
    }

    private IReadOnlyList<ArchiveRecord> ParsePicked(string response, IReadOnlyList<ArchiveRecord> candidates)
    {
        // RecallDepth was previously enforced by the prompt alone: a model
        // that returned more indices than it was asked for got all of them,
        // and the knob quietly meant nothing. Librarian has always capped in
        // code; this makes the pair consistent. The instruction asks for the
        // best first, so a reply over the cap loses its weakest picks.
        var picked = new List<ArchiveRecord>();
        foreach (var i in InstructionFile.Indices(response))
        {
            if (picked.Count >= _knobs.RecallDepth)
            {
                break;
            }

            if (i >= 0 && i < candidates.Count)
            {
                picked.Add(candidates[i]);
            }
        }

        return picked;
    }
}
