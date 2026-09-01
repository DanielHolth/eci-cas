using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Reasoning;

/// <summary>
/// Selector only: picks which of the archive's known (Category, Topic)
/// pairs might hold background relevant to this turn. Subtopic is
/// deliberately absent from what it is shown: resolving which subtopic
/// matters is Recall's job, reading actual rows, and keeping it out here
/// keeps the selection prompt short as the archive deepens. No advice
/// text published anymore — Intent owns all advisory framing; Recall (M4) is
/// the one that actually reads rows once a pair is selected.
///
/// Doesn't use CognitiveAgent&lt;T&gt;'s BuildPrompt template method: the
/// selection prompt needs the store's cached index read first, which
/// BuildPrompt's synchronous signature can't express. HandleAsync is
/// overridden directly instead, replicating the substrate-call/log/fallback
/// shape inline. BuildPrompt/ParseResult/FallbackResult below exist only to
/// satisfy CognitiveAgent&lt;T&gt;'s abstract contract and are never invoked.
/// </summary>
public sealed class ReasoningAgent : CognitiveAgent<IReadOnlyList<ArchivePair>>
{
    /// <summary>Selected pairs, carried on the events.selected-pairs envelope's Meta.</summary>
    public const string SelectedPairsKey = "reasoning.selected_pairs";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ReasoningOptions _options;
    private readonly ILogger _logger;

    public ReasoningAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ReasoningAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ReasoningOptions> options)
        : base(bus, activity, logger, substrate, agentSubstrates)
    {
        _bus = bus;
        _store = store;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Reasoning";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope) =>
        throw new NotSupportedException($"{Name} overrides HandleAsync directly — see class remarks.");

    protected override IReadOnlyList<ArchivePair> ParseResult(SubstrateResult result) => [];

    protected override IReadOnlyList<ArchivePair> FallbackResult(Envelope envelope) => [];

    protected override void Publish(Envelope envelope, string prompt, IReadOnlyList<ArchivePair> result, SubstrateResult? diagnostics)
    {
        // Always published, even empty on fallback/no-index/no-signal text —
        // Recall's roster slot in Governance's bundle needs a reply every
        // time, or the bundle would only ever complete via timeout.
        //
        // TextKey is carried forward explicitly: Envelope.Derive starts a
        // fresh Meta rather than merging the parent's, so without this
        // Recall's picking prompt would have no idea what was actually asked
        // and could only rank candidates by generic importance — which is how
        // an unrelated "system/.../name" row used to outrank everything for
        // a question about the human's own name.
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var meta = MetaBag.Empty.With(SelectedPairsKey, result).With(PerceptionAgent.TextKey, text);

        // The profile rides along for the same reason: it decides which
        // archive tier Recall reads, and Derive would otherwise drop it.
        if (envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) is { Length: > 0 } profileId)
        {
            meta = meta.With(PerceptionAgent.ProfileKey, profileId);
        }

        var selection = envelope.Derive(Topics.SelectedPairs, Name, envelope.Severity, meta);
        _bus.Publish(Topics.SelectedPairs, selection);
    }

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var index = _store.IndexFor(envelope.Meta.Get<string>(PerceptionAgent.ProfileKey));
        if (index.Count == 0)
        {
            Publish(envelope, string.Empty, [], diagnostics: null);
            return;
        }

        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));
        var prompt = BuildSelectionPrompt(text, index);

        try
        {
            var result = await _substrate.CompleteAsync(entry.Class, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            Publish(envelope, prompt, ParsePairs(result.Text, index), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} substrate call failed, fallback posture {Posture}", Name, Fallback);
            Publish(envelope, prompt, [], diagnostics: null);
        }
    }

    private string BuildSelectionPrompt(string? text, IReadOnlyList<ArchivePair> index)
    {
        var options = string.Join("\n", index.Select((t, i) => $"{i}. {t.Category}/{t.Topic}"));
        return $"""
            Known knowledge-base topics (index: category/topic):
            {options}

            For this turn's text, pick up to {_options.MaxSelectedPairs} of the
            topics above that could hold relevant background — respond with just
            their index numbers, comma-separated (e.g. "0, 3"). If none are
            relevant, respond with nothing.

            Turn: {text}
            """;
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
