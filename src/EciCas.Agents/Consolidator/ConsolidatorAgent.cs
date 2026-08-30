using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Consolidator;

/// <summary>
/// Parallel publisher on events.bundle alongside Intent — never through the
/// live reply path (see plan's opening rationale: this is exactly the hop
/// that broke the Python bus). Batches bundle content into ArchiveRecords and
/// flushes to the store every BatchSize bundles, then announces the epoch on
/// system.control so Self can invalidate its persona cache.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: the deterministic write always happens regardless
/// of substrate use, and results batch rather than publish one-shot, neither
/// of which fits that base class's model. When AgentSubstrates:Agents'
/// Consolidator entry has UseSubstrate true, an extra substrate call proposes
/// richer facts grounded in Recall's own lookup results — already present on
/// this same Bundle envelope via GovernanceAgent.BuildBundleMeta — biasing it
/// to reuse paths Recall just showed it instead of minting near-duplicates.
/// </summary>
public sealed class ConsolidatorAgent : AgentBase, ICognitiveAgent
{
    public const string ControlKindKey = "control.kind";
    public const string EpochIdKey = "control.epoch_id";
    public const string WrittenKind = "Written";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ConsolidatorOptions _options;
    private readonly ILogger _logger;
    private readonly List<ArchiveRecord> _pending = [];
    private readonly object _pendingLock = new();

    public ConsolidatorAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ConsolidatorAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ConsolidatorOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Consolidator";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        // Write under the same significant-word paths Reasoning proposes when
        // querying (see SignificantWords) so a later lookup actually
        // intersects what got stored here — plus a fixed "turn" anchor so
        // short/low-signal turns are still recoverable by category.
        var paths = SignificantWords.Extract(text).Append("turn").Distinct();
        var newRecords = paths.Select(path => new ArchiveRecord(path, text, envelope.Timestamp)).ToList();

        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        if (entry.UseSubstrate)
        {
            newRecords.AddRange(await ExtractFactsAsync(envelope, text, entry.Class, cancellationToken).ConfigureAwait(false));
        }

        List<ArchiveRecord>? batch = null;
        lock (_pendingLock)
        {
            _pending.AddRange(newRecords);
            if (_pending.Count >= _options.BatchSize)
            {
                batch = [.. _pending];
                _pending.Clear();
            }
        }

        if (batch is null)
        {
            return;
        }

        await _store.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Agent} wrote {Count} records: {Paths}",
            Name, batch.Count, string.Join(", ", batch.Select(r => r.Path)));

        var epochId = Guid.NewGuid();
        var written = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ControlKindKey, WrittenKind).With(EpochIdKey, epochId));
        _bus.Publish(Topics.SystemControl, written);
    }

    /// <summary>
    /// A broken or unavailable substrate call must never block the
    /// deterministic write above — errors are logged and swallowed, same
    /// posture as FallbackPosture.Closed on CognitiveAgent&lt;T&gt;.
    /// </summary>
    private async Task<IReadOnlyList<ArchiveRecord>> ExtractFactsAsync(Envelope envelope, string text, string substrateClass, CancellationToken cancellationToken)
    {
        var known = envelope.Meta.Get<string>(RecallAgent.ResultsKey) ?? "nothing on file";
        var prompt = $"""
            Known facts under related topics: {known}

            From this turn, extract any new facts worth remembering long-term that
            aren't already covered above. Respond with zero or more lines, each
            formatted as "path: content" (e.g. "person/family/marcus: birth_date = 2020-08-28").
            Reuse a path from the known facts above when the subject clearly matches;
            only invent a new path for a genuinely new subject. If there's nothing new, respond with nothing.

            Turn: {text}
            """;

        try
        {
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            return ParseFacts(result.Text, envelope.Timestamp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} fact-extraction substrate call failed, skipping", Name);
            return [];
        }
    }

    private static List<ArchiveRecord> ParseFacts(string response, DateTimeOffset timestamp)
    {
        var records = new List<ArchiveRecord>();
        foreach (var line in response.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var path = line[..separator].Trim();
            var content = line[(separator + 1)..].Trim();
            if (path.Length > 0 && content.Length > 0)
            {
                records.Add(new ArchiveRecord(path, content, timestamp));
            }
        }

        return records;
    }
}
