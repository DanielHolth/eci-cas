using System.Diagnostics;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Librarian;

/// <summary>
/// Was Reasoning until the name was audited against the job: this agent
/// reasons about nothing and reads no fact. It interprets the turn and
/// points Recall at a shelf — the catalogue half of a librarian, never the
/// fetching half, which is Recall's. "Reasoning" also overclaimed into
/// Intent's territory, and "Retrieval" would have collided with Recall,
/// which is the agent that actually retrieves.
///
/// Selector only: picks which of the archive's known (Category, Topic)
/// pairs might hold background relevant to this turn. Subtopic is
/// deliberately absent from what it is shown: resolving which subtopic
/// matters is Recall's job, reading actual rows, and keeping it out here
/// keeps the selection prompt short as the archive deepens. No advice
/// text published anymore — Intent owns all advisory framing; Recall is
/// the one that actually reads rows once a pair is selected.
///
/// Doesn't use CognitiveAgent&lt;T&gt;'s BuildPrompt template method: the
/// selection prompt needs the store's cached index read first, which
/// BuildPrompt's synchronous signature can't express. HandleAsync is
/// overridden directly instead, replicating the substrate-call/log/fallback
/// shape inline. BuildPrompt/ParseResult/FallbackResult below exist only to
/// satisfy CognitiveAgent&lt;T&gt;'s abstract contract and are never invoked.
/// </summary>
public sealed class LibrarianAgent : CognitiveAgent<IReadOnlyList<ArchivePair>>
{
    /// <summary>Selected pairs, carried on the events.selected-pairs envelope's Meta.</summary>
    public const string SelectedPairsKey = "librarian.selected_pairs";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly LibrarianOptions _options;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IPassageStore _passages;
    private readonly PassageOptions _passageOptions;
    private readonly ILogger _logger;

    public LibrarianAgent(IMessageBus bus, BusActivityTracker activity, ILogger<LibrarianAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<LibrarianOptions> options,
        IEmbeddingProvider embeddings, IPassageStore passages, IOptions<PassageOptions> passageOptions,
        IInstructionStore instructions)
        : base(bus, activity, logger, substrate, agentSubstrates)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _embeddings = embeddings;
        _passages = passages;
        _passageOptions = passageOptions.Value;
        _logger = logger;
    }

    public override string Name => "Librarian";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope) =>
        throw new NotSupportedException($"{Name} overrides HandleAsync directly — see class remarks.");

    protected override IReadOnlyList<ArchivePair> ParseResult(SubstrateResult result) => [];

    protected override IReadOnlyList<ArchivePair> FallbackResult(Envelope envelope) => [];

    /// <summary>
    /// Never reached: HandleAsync is overridden, so nothing in the base class
    /// ever calls this. Kept to satisfy CognitiveAgent<T>, and delegating
    /// rather than throwing so a future base-class path degrades to "no
    /// passages" instead of an exception.
    /// </summary>
    protected override void Publish(Envelope envelope, string prompt, IReadOnlyList<ArchivePair> result, SubstrateResult? diagnostics, string? degraded) =>
        Publish(envelope, result, degraded);

    private void Publish(Envelope envelope, IReadOnlyList<ArchivePair> result, string? degraded)
    {
        // Always published, even empty on fallback/no-index/no-signal text —
        // Recall's roster slot in Governance's bundle needs a reply every
        // time, or the bundle would only ever complete via timeout.
        //
        // TextKey is carried forward explicitly: Envelope.Derive starts a
        // fresh Meta rather than merging the parent's, so without this
        // Recall's picking prompt would have no idea what was actually asked
        // and could only rank candidates by generic importance — which is how
        // an unrelated "assistant/.../name" row used to outrank everything for
        // a question about the human's own name.
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var meta = SubstrateHealth.Mark(MetaBag.Empty.With(SelectedPairsKey, result).With(PerceptionAgent.TextKey, text), degraded);

        // The profile rides along for the same reason: it decides which
        // archive tier Recall reads, and Derive would otherwise drop it.
        if (envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) is { Length: > 0 } profileId)
        {
            meta = meta.With(PerceptionAgent.ProfileKey, profileId);
        }

        var selection = envelope.Derive(Topics.SelectedPairs, Name, envelope.Severity, meta);

        _logger.LogDebug("{Agent} selected {Count} pair(s): {Pairs}", Name, result.Count,
            result.Count == 0 ? "none" : string.Join(", ", result.Select(p => $"{p.Category}/{p.Topic}")));

        _bus.Publish(Topics.SelectedPairs, selection);
    }

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var index = _store.IndexFor(envelope.Meta.Get<string>(PerceptionAgent.ProfileKey));
        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));

        _logger.LogDebug("{Agent} index holds {Count} pair(s): {Pairs}", Name, index.Count,
            string.Join(", ", index.Select(p => $"{p.Category}/{p.Topic}")));

        // The vector half, and it runs first because it is the cheap one: a
        // local embedding and a cosine sweep, no substrate call and no tier.
        // Whatever it finds is merged into the selection below rather than
        // replacing it — a passage says "last time this came up I should have
        // read X", which is a lead, not a verdict on everything else.
        var remembered = await SearchPassageLeadsAsync(text, index, cancellationToken).ConfigureAwait(false);

        // An empty archive and a deliberately deterministic agent reach the
        // same place — nothing to select — and neither is a degradation. The
        // passages still go out: matching one costs no substrate call, so
        // there is no reason a deterministic Librarian should lose them.
        if (index.Count == 0 || !entry.UseSubstrate)
        {
            Publish(envelope, remembered, degraded: null);
            return;
        }

        // A whole index that already fits under the cap makes the selection
        // call pure overhead: the best it could return is a subset of what
        // passing everything gives Recall, and Recall filters row by row
        // anyway. Dropping it removes one of the turn's three serial
        // substrate calls outright — which is every turn on a young archive.
        if (index.Count <= _options.MaxSelectedPairs)
        {
            Publish(envelope, index, degraded: null);
            return;
        }

        var prompt = BuildSelectionPrompt(text, index);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _substrate.CompleteAsync(entry.Class, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            Publish(envelope, Merge(ParsePairs(result.Text, index), remembered), degraded: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} substrate call {Cause} after {LatencyMs}ms, fallback posture {Posture}",
                Name, cause, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Fallback);

            // Open posture, and the passages are exactly what makes it worth
            // something: a selection call that failed still leaves the turn
            // with whatever the persona previously learned to look up here.
            Publish(envelope, remembered, cause);
        }
    }

    /// <summary>
    /// Cosine top-K over the passage corpus, keeping only the archive pairs
    /// those notes named. The note TEXT is not read here — that is
    /// HindsightAgent's, and it reaches Intent as its own bundle slot. What
    /// stays behind is the lead: "when something like this came up, I wished
    /// I had read person/family", which is a statement about where the facts
    /// are and therefore Librarian's business.
    ///
    /// The cost of the split is one extra embed per turn, since Hindsight
    /// embeds the same text for its own sweep. A local ONNX call, and worth
    /// it rather than having one agent's envelope carry the other's payload;
    /// sharing a per-turn embedding is a later optimisation (roadmap.md).
    ///
    /// Unavailable embeddings are a normal state, not a failure: no model
    /// downloaded, provider set to "none", or an embedding endpoint that just
    /// refused. All of them return nothing here and the turn proceeds on the
    /// pre-vector path, which is why this is not marked substrate.degraded —
    /// the persona is not thinking with a faculty missing, it is thinking
    /// without a shortcut it may never have had.
    /// </summary>
    private async Task<IReadOnlyList<ArchivePair>> SearchPassageLeadsAsync(
        string? text, IReadOnlyList<ArchivePair> index, CancellationToken cancellationToken)
    {
        if (!_embeddings.Available || string.IsNullOrWhiteSpace(text) || _passageOptions.TopK <= 0)
        {
            return [];
        }

        var query = await _embeddings.EmbedAsync([text], cancellationToken).ConfigureAwait(false);
        if (query.Count == 0)
        {
            return [];
        }

        var hits = await _passages.SearchAsync(query[0], _passageOptions.TopK, _passageOptions.MinScore, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        // A passage names the pairs that existed when it was written, and the
        // index is the only authority on what exists now — deleting a pair's
        // last row deletes its file, which is how a pair leaves the index. So
        // pointers are resolved against the live index rather than trusted,
        // and a stale one drops silently instead of sending Recall to read
        // nothing. A note may also name no pair at all, which costs nothing:
        // its text still reaches Intent through Hindsight.
        var known = index.ToDictionary(p => $"{p.Category}/{p.Topic}", StringComparer.OrdinalIgnoreCase);
        var pairs = hits
            .SelectMany(h => h.Passage.Pairs)
            .Select(p => known.GetValueOrDefault($"{p.Category}/{p.Topic}"))
            .OfType<ArchivePair>()
            .Distinct()
            .Take(_passageOptions.MaxPairsFromPassages)
            .ToList();

        _logger.LogInformation("{Agent} matched {Count} note(s) for {Pairs} lead(s)", Name, hits.Count, pairs.Count);
        _logger.LogDebug("{Agent} passage hits: {Hits}", Name,
            string.Join(" | ", hits.Select(h => $"{h.Score:F3} {h.Passage.Text} -> [{string.Join(", ", h.Passage.Pairs.Select(p => $"{p.Category}/{p.Topic}"))}]")));

        return pairs;
    }


    /// <summary>Selection first, passage leads after, no duplicates — the LLM saw the whole index, a passage saw one past turn.</summary>
    private static IReadOnlyList<ArchivePair> Merge(IReadOnlyList<ArchivePair> selected, IReadOnlyList<ArchivePair> remembered) =>
        [.. selected, .. remembered.Where(p => !selected.Contains(p))];

    private string BuildSelectionPrompt(string? text, IReadOnlyList<ArchivePair> index)
    {
        var options = string.Join("\n", index.Select((t, i) => $"{i}. {t.Category}/{t.Topic}"));
        return InstructionFile.Fill(_instructions.For(Name),
            ("options", options),
            ("max", _options.MaxSelectedPairs.ToString()),
            ("text", text ?? string.Empty));
    }

    private static IReadOnlyList<ArchivePair> ParsePairs(string response, IReadOnlyList<ArchivePair> index)
    {
        var selected = new List<ArchivePair>();
        foreach (var token in response.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var i) && i >= 0 && i < index.Count)
            {
                selected.Add(index[i]);
            }
        }

        return selected;
    }
}
