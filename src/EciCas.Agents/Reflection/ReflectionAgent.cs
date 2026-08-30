using System.Text.Json;
using EciCas.Agents.Consolidator;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Reflection;

/// <summary>
/// Buffers concluded turns (same _pending shape as ConsolidatorAgent) and,
/// once ReflectionOptions.BatchSize accumulates, makes one substrate call
/// scoring candidate follow-up ideas. The best-ranked candidate is pushed
/// back onto events.perception — downstream nothing knows the difference
/// from external input (plan §3.6) — only when persona drive-vector state
/// (read from IArchiveStore at ImpulseAgent.DrivePath, never a direct
/// reference to ImpulseAgent) reads eager enough; otherwise it's archived
/// quietly like every other candidate. See roadmap.md's "Reflection Agent
/// redesign (drive-gated, batched)".
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: one substrate call per BATCH, not per envelope,
/// doesn't fit that base class's model — same rationale ConsolidatorAgent's
/// own doc comment gives for its choice.
///
/// Generation still guards against an idea -> arc -> conclusion -> idea loop
/// spending forever on LLM calls: a batch whose max generation is already at
/// MaxIdeaGeneration is scored (so nothing is lost) but never pushed.
/// </summary>
public sealed class ReflectionAgent : AgentBase, ICognitiveAgent
{
    public const string TriggeredByKey = "perception.triggered_by";
    public const string SourceTypeKey = "perception.source_type";
    public const string ReflectedKind = "Reflected";

    private const string FixedCategory = "self";
    private const string FixedTopic = "reflection";
    private const string FixedSubject = "self";
    private const string FixedKey = "insight";
    private const double QuietImportance = 0.1;
    private const double PushedImportance = 0.2;

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly IAgentStateStore _stateStore;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ILogger _logger;
    private readonly ReflectionOptions _options;
    private readonly List<BufferedConclusion> _pending = [];
    private readonly object _pendingLock = new();

    public ReflectionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ReflectionAgent> logger, IArchiveStore store, IAgentStateStore stateStore,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ReflectionOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _stateStore = stateStore;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _logger = logger;
        _options = options.Value;
    }

    public override string Name => "Reflection";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Conclusion];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;

        List<BufferedConclusion>? batch = null;
        lock (_pendingLock)
        {
            _pending.Add(new BufferedConclusion(reply, envelope.Generation));
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

        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<BufferedConclusion> batch, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        List<Candidate> candidates;
        try
        {
            var result = await _substrate.CompleteAsync(entry.Class, BuildBatchPrompt(batch), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            candidates = ParseCandidates(result.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Closed fallback posture: a broken substrate call skips this
            // flush entirely — nothing pushed, nothing archived — rather
            // than guessing at an idea. Matches ConsolidatorAgent's
            // ExtractFactsAsync swallow-and-log for the same failure mode.
            _logger.LogWarning(ex, "{Agent} batch-scoring substrate call failed, skipping flush", Name);
            return;
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var best = candidates.MaxBy(c => c.Score)!;
        var maxGeneration = batch.Max(b => b.Generation);
        var eagerness = await GetEagernessAsync(cancellationToken).ConfigureAwait(false);
        var shouldPush = maxGeneration < _options.MaxIdeaGeneration && eagerness >= _options.EagernessThreshold;

        var now = DateTimeOffset.UtcNow;
        // Category/Topic are fixed (self/reflection); Subtopic is the LLM's
        // own free-text functional label per candidate — not constrained to
        // an enumerated list, so nuance isn't lost to a fixed vocabulary.
        // Importance is fixed by role (quiet vs. pushed), not LLM-scored —
        // candidate.Score stays purely for picking `best` among the batch.
        var internalRecords = candidates
            .Select(candidate => new ArchiveRecord(
                FixedCategory, FixedTopic, candidate.Subtopic, FixedSubject, FixedKey, candidate.Idea,
                now, ArchiveDomain.Internal, candidate == best && shouldPush ? PushedImportance : QuietImportance))
            .ToList();
        await _store.WriteAsync(internalRecords, cancellationToken).ConfigureAwait(false);

        if (shouldPush)
        {
            _logger.LogInformation("{Agent} pushed idea: {Idea}", Name, best.Idea);
            var idea = Envelope.Create(Topics.Perception, Name, Severity.Restful,
                MetaBag.Empty.With(PerceptionAgent.TextKey, best.Idea).With(TriggeredByKey, "self").With(SourceTypeKey, "idea"),
                generation: maxGeneration + 1);
            _bus.Publish(Topics.Perception, idea);
        }

        var reflected = Envelope.Create(Topics.SystemControl, Name, Severity.Neutral,
            MetaBag.Empty.With(ConsolidatorAgent.ControlKindKey, ReflectedKind));
        _bus.Publish(Topics.SystemControl, reflected);
    }

    private async Task<double> GetEagernessAsync(CancellationToken cancellationToken)
    {
        var records = await _stateStore.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
        var vectors = records.Count > 0
            ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors()
            : new DriveVectors();

        // Ports Python's `engagement` appraisal axis (curiosity - 0.4*fatigue)
        // from agents/impulse/agent.py — the closest existing analog to "eager
        // enough to share an idea"; no new formula invented.
        return Math.Clamp(vectors.Curiosity - 0.4 * vectors.Fatigue, 0.0, 1.0);
    }

    private static string BuildBatchPrompt(List<BufferedConclusion> batch)
    {
        var turns = string.Join("\n", batch.Select((b, i) => $"{i + 1}. {PromptCap.Apply(b.ReplyText)}"));
        return $"""
            From these recent replies, propose follow-up thoughts or questions worth
            exploring later. Respond with zero or more lines, each formatted as
            "score|subtopic|idea" where score is 0.0-1.0 insight-worthiness and
            subtopic is a short (1-2 word) functional label for the kind of
            thought it is — pick whatever label fits best, e.g. pattern,
            hypothesis, meta-rule, synthesis, question, or another label of your
            own choosing (e.g. "0.7|hypothesis|whether the trip dates still work
            with the deadline"). If nothing stands out, respond with nothing.

            Replies:
            {turns}
            """;
    }

    private static List<Candidate> ParseCandidates(string response)
    {
        var candidates = new List<Candidate>();
        foreach (var line in response.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length != 3)
            {
                continue;
            }

            var scoreText = parts[0].Trim();
            var subtopic = parts[1].Trim();
            var idea = parts[2].Trim();
            if (idea.Length > 0 && subtopic.Length > 0 && double.TryParse(scoreText, out var score))
            {
                candidates.Add(new Candidate(Math.Clamp(score, 0.0, 1.0), subtopic, idea));
            }
        }

        return candidates;
    }

    private sealed record BufferedConclusion(string ReplyText, int Generation);
    private sealed record Candidate(double Score, string Subtopic, string Idea);
}
