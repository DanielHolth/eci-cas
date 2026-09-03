using EciCas.Agents.Perception;
using EciCas.Agents.Librarian;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    private const int MaxPickedPerWorker = 5;

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly RecallOptions _options;
    private readonly ILogger _logger;

    public RecallAgent(IMessageBus bus, BusActivityTracker activity, ILogger<RecallAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<RecallOptions> options,
        IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
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

        // Nothing selected and no-substrate-by-configuration land in the same
        // place — no facts, and neither is a degradation.
        if (pairs.Count == 0 || !entry.UseSubstrate)
        {
            Publish(envelope, [], degraded: null);
            return;
        }

        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));

        // Phase one: read every selected pair at once. Distinct pairs are
        // distinct files, so these don't contend with each other.
        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        var loaded = await Task.WhenAll(pairs.Select(p => _store.LookupAsync(p, profileId, cancellationToken))).ConfigureAwait(false);

        // Same rule Librarian applies to the index, one stage down: when
        // every loaded row would fit in a single worker's pick budget, the
        // picking call can only return a subset of what passing them all
        // gives Intent. Skipping it removes the second of the turn's three
        // serial substrate calls on a young archive. Order is preserved —
        // the store already sorted by Importance.
        // Guarded because the join is an argument, not part of the template:
        // it walks every loaded row on every turn at every level, and only
        // Debug ever reads the result.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} read {Rows} row(s) from {Pairs}: {Loaded}",
                Name, loaded.Sum(rows => rows.Count), pairs.Count,
                string.Join(" | ", loaded.SelectMany(rows => rows).Select(Describe)));
        }

        var total = loaded.Sum(rows => rows.Count);
        if (total <= MaxPickedPerWorker)
        {
            Publish(envelope, [.. loaded.SelectMany(rows => rows).OrderByDescending(r => r.Importance)], degraded: null);
            return;
        }

        // Phase two: one flat set of workers over every chunk of every pair.
        var chunks = Chunks(loaded);
        var results = await Task.WhenAll(chunks.Select(c => PickAsync(c, text, entry.Class, cancellationToken))).ConfigureAwait(false);
        var picked = results.SelectMany(r => r.Facts).OrderByDescending(r => r.Importance).ToList();

        // Any worker failing means some of the archive went unread, so the
        // turn is grounded in less than it should have been — one failure is
        // enough to say so, and the first cause is as good as any.
        var degraded = results.Select(r => r.Degraded).FirstOrDefault(c => c is not null);

        // One line for the whole turn, not one per worker: aggregate latency
        // (wall-clock across the parallel calls, not summed) and sum tokens/
        // cost across every worker that actually reached the substrate.
        var diagnostics = results.Select(r => r.Diagnostics).Where(d => d is not null).Select(d => d!).ToList();
        if (diagnostics.Count > 0)
        {
            _logger.LogInformation("{Agent} picking calls ({Workers} workers, {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost)",
                Name, diagnostics.Count, diagnostics.Max(d => d.Latency.TotalMilliseconds), diagnostics.Sum(d => d.TokenCount), diagnostics.Sum(d => d.Cost));
        }

        Publish(envelope, picked, degraded);
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

        return chunks.Count <= _options.MaxConcurrentRecalls
            ? chunks
            : [.. chunks.Take(_options.MaxConcurrentRecalls)];
    }

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
    /// chunk, no retry, no turn-level failure. Diagnostics travel back with
    /// the facts instead of being logged here, so HandleAsync can fold every
    /// worker's numbers into one line per turn.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics, string? Degraded)> PickAsync(
        IReadOnlyList<ArchiveRecord> candidates, string text, string substrateClass, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return ([], null, null);
        }

        try
        {
            var prompt = BuildPrompt(text, candidates);
            _logger.LogDebug("{Agent} picking prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} picking response <<<\n{Response}", Name, result.Text);
            return (ParsePicked(result.Text, candidates), result, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var first = candidates[0];
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} lookup for {Category}/{Topic} {Cause}, skipping", Name, first.Category, first.Topic, cause);
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
        var rows = string.Join("\n", candidates.Select((r, i) => $"{i}. {r.Subtopic} / {r.Subject} {r.Key} = {r.Value}"));
        return InstructionFile.Fill(_instructions.For(Name),
            ("rows", rows),
            ("max", MaxPickedPerWorker.ToString()),
            ("text", text));
    }

    private static IReadOnlyList<ArchiveRecord> ParsePicked(string response, IReadOnlyList<ArchiveRecord> candidates)
    {
        var picked = new List<ArchiveRecord>();
        foreach (var token in response.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var i) && i >= 0 && i < candidates.Count)
            {
                picked.Add(candidates[i]);
            }
        }

        return picked;
    }
}
