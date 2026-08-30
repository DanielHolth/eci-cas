using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Recall;

/// <summary>
/// Second stage of the knowledge swarm: for each triple Reasoning selected,
/// reads that category's rows from the store (pre-trimmed to
/// RecallOptions.MaxPerTopic, already sorted by Importance), then fires one
/// substrate call scoped only to that triple's candidates asking which rows
/// are actually relevant. All triples run in parallel inside one
/// HandleAsync. Results across triples are aggregated into a single list —
/// no External/Internal split, since Category=self from Reflection's own
/// writes already implies "internal" and both sort by Importance together.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: N parallel substrate calls per envelope doesn't
/// fit that base class's one-call model — same rationale ConsolidatorAgent's
/// own doc comment gives for its choice.
/// </summary>
public sealed class RecallAgent : AgentBase, ICognitiveAgent
{
    public const string RecalledFactsKey = "recall.facts";

    private const int MaxPickedPerTriple = 5;

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly RecallOptions _options;
    private readonly ILogger _logger;

    public RecallAgent(IMessageBus bus, BusActivityTracker activity, ILogger<RecallAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<RecallOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Recall";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.SelectedTriples];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var triples = envelope.Meta.Get<IReadOnlyList<ArchiveTriple>>(ReasoningAgent.SelectedTriplesKey) ?? [];

        if (triples.Count == 0)
        {
            Publish(envelope, []);
            return;
        }

        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));
        var perTripleResults = await Task.WhenAll(triples.Select(t => PickAsync(t, text, entry.Class, cancellationToken))).ConfigureAwait(false);
        var picked = perTripleResults.SelectMany(r => r.Facts).OrderByDescending(r => r.Importance).ToList();

        // One line for the whole turn, not one per triple: aggregate latency
        // (wall-clock across the parallel calls, not summed) and sum tokens/
        // cost across every triple that actually reached the substrate.
        var diagnostics = perTripleResults.Select(r => r.Diagnostics).Where(d => d is not null).Select(d => d!).ToList();
        if (diagnostics.Count > 0)
        {
            var paths = picked.Count == 0
                ? "nothing on file"
                : string.Join(", ", picked.Select(f => $"{f.Category}/{f.Topic}/{f.Subtopic}/{f.Subject}/{f.Key} = {f.Value}"));
            _logger.LogInformation("{Agent} {Paths} ({LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost)",
                Name, paths, diagnostics.Max(d => d.Latency.TotalMilliseconds), diagnostics.Sum(d => d.TokenCount), diagnostics.Sum(d => d.Cost));
        }

        Publish(envelope, picked);
    }

    private void Publish(Envelope envelope, IReadOnlyList<ArchiveRecord> facts)
    {
        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(RecalledFactsKey, facts));
        _bus.Publish(Topics.Advisories, advisory);
    }

    /// <summary>
    /// One substrate call scoped to a single triple's candidates. Failure is
    /// non-gating and isolated: a broken call contributes nothing for this
    /// triple, no retry, no turn-level failure. Diagnostics travel back with
    /// the facts instead of being logged here, so HandleAsync can fold every
    /// triple's numbers into one line per turn.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics)> PickAsync(ArchiveTriple triple, string text, string substrateClass, CancellationToken cancellationToken)
    {
        var candidates = await _store.LookupAsync(triple, _options.MaxPerTopic, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return ([], null);
        }

        try
        {
            var result = await _substrate.CompleteAsync(substrateClass, BuildPrompt(text, candidates), cancellationToken).ConfigureAwait(false);
            return (ParsePicked(result.Text, candidates), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} lookup for {Category}/{Topic}/{Subtopic} failed, skipping", Name, triple.Category, triple.Topic, triple.Subtopic);
            return ([], null);
        }
    }

    private static string BuildPrompt(string text, IReadOnlyList<ArchiveRecord> candidates)
    {
        // Category/Topic/Subtopic withheld — redundant with scope. Timestamp/
        // Domain/Importance withheld to keep context lean. Rows are already
        // pre-sorted by Importance by the store, not re-sorted here.
        //
        // The turn's own text is included so picking is relevance-to-THIS-
        // question, not just "important in general" — without it a category
        // like "system" (the assistant's own name/traits) would look just as
        // pickable for a question about the HUMAN's name as an actual
        // person-category row, since nothing here ranked one over the other.
        var rows = string.Join("\n", candidates.Select((r, i) => $"{i}. {r.Subject} {r.Key} = {r.Value}"));
        return $"""
            Candidate facts (index: subject key = value), most important first:
            {rows}

            Pick up to {MaxPickedPerTriple} rows that actually help answer this
            turn — respond with just their index numbers, comma-separated
            (e.g. "0, 2"). A row is only relevant if it's about the same thing
            being asked about (e.g. a fact about the assistant itself does not
            answer a question about the human, and vice versa). If none are
            relevant, respond with nothing.

            Turn: {text}
            """;
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
