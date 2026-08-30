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

        var perTripleResults = await Task.WhenAll(triples.Select(t => PickAsync(t, entry.Class, cancellationToken))).ConfigureAwait(false);
        var picked = perTripleResults.SelectMany(r => r).OrderByDescending(r => r.Importance).ToList();

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
    /// triple, no retry, no turn-level failure.
    /// </summary>
    private async Task<IReadOnlyList<ArchiveRecord>> PickAsync(ArchiveTriple triple, string substrateClass, CancellationToken cancellationToken)
    {
        var candidates = await _store.LookupAsync(triple, _options.MaxPerTopic, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return [];
        }

        try
        {
            var result = await _substrate.CompleteAsync(substrateClass, BuildPrompt(candidates), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            return ParsePicked(result.Text, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} lookup for {Category}/{Topic}/{Subtopic} failed, skipping", Name, triple.Category, triple.Topic, triple.Subtopic);
            return [];
        }
    }

    private static string BuildPrompt(IReadOnlyList<ArchiveRecord> candidates)
    {
        // Category/Topic/Subtopic withheld — redundant with scope. Timestamp/
        // Domain/Importance withheld to keep context lean. Rows are already
        // pre-sorted by Importance by the store, not re-sorted here.
        var rows = string.Join("\n", candidates.Select((r, i) => $"{i}. {r.Subject} {r.Key} = {r.Value}"));
        return $"""
            Candidate facts (index: subject key = value), most important first:
            {rows}

            Pick up to {MaxPickedPerTriple} rows actually relevant right now —
            respond with just their index numbers, comma-separated (e.g. "0, 2").
            If none are relevant, respond with nothing.
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
