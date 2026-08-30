using System.Collections.Concurrent;
using System.Text.Json;
using EciCas.Agents.Consolidator;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Governance;

/// <summary>
/// Decision-only: bundles the advisory fan-out, gates Action on Security's
/// verdict (green/yellow/red per plan §3.3's matrix), and produces the
/// conclusion. Nothing else.
/// </summary>
public sealed class GovernanceAgent : AgentBase
{
    /// <summary>Carries Security's concern into a re-issued Bundle so Intent can revise. Set only on a revision pass.</summary>
    public const string RevisionConcernKey = "governance.revision_concern";

    /// <summary>
    /// system.control kind published on a Red verdict — mirrors
    /// ConsolidatorAgent.WrittenKind/ReflectionAgent.ReflectedKind's
    /// convention. ImpulseAgent listens for this to apply FrustrationNudge,
    /// with no direct reference between the two agents.
    /// </summary>
    public const string FrustrationKind = "Frustration";

    /// <summary>The face the persona's current drive-vector state implies, attached to a blocked Action/Conclusion so what reaches the human at least matches how it feels.</summary>
    public const string ExpressionKey = "governance.expression";

    /// <summary>Set true on a blocked Action/Conclusion so downstream consumers can tell a security block from an ordinary reply without inspecting VerdictKey.</summary>
    public const string SecurityAlertKey = "governance.security_alert";

    private const string SecurityAlertPathAnchor = "security_alert";

    private readonly IMessageBus _bus;
    private readonly ILogger<GovernanceAgent> _logger;
    private readonly GovernanceOptions _options;
    private readonly IAgentStateStore _store;
    private readonly ConcurrentDictionary<Guid, BundleState> _bundles = new();

    public GovernanceAgent(IMessageBus bus, BusActivityTracker activity, ILogger<GovernanceAgent> logger, IOptions<GovernanceOptions> options, IAgentStateStore store)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _logger = logger;
        _options = options.Value;
        _store = store;
    }

    public override string Name => "Governance";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.Advisories, Topics.Verdict];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Topic)
        {
            case Topics.Perception:
                OnPerception(envelope);
                break;
            case Topics.Advisories:
                OnAdvisory(envelope);
                break;
            case Topics.Verdict:
                await OnVerdictAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void OnPerception(Envelope perception)
    {
        var state = _bundles.GetOrAdd(perception.CorrelationId, _ => new BundleState());

        bool justArrived;
        lock (state)
        {
            justArrived = state.Perception is null;
            state.Perception = perception;
        }

        if (justArrived)
        {
            _ = RunTimeoutAsync(state);
        }

        TryComplete(state);
    }

    private void OnAdvisory(Envelope advisory)
    {
        // GetOrAdd, not TryGetValue: Governance runs one consumer loop per
        // subscribed topic, so an advisory can arrive before this agent's own
        // Perception-topic loop has processed the originating event. Without
        // this, that advisory would be silently and permanently dropped.
        var state = _bundles.GetOrAdd(advisory.CorrelationId, _ => new BundleState());

        lock (state)
        {
            state.Advisories[advisory.PublishedBy] = advisory;
        }

        TryComplete(state);
    }

    private async Task RunTimeoutAsync(BundleState state)
    {
        try
        {
            await Task.Delay(_options.BundleTimeoutMs, state.TimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TryComplete(state, forced: true);
    }

    private void TryComplete(BundleState state, bool forced = false)
    {
        Envelope perception;
        List<Envelope> advisorySnapshot;
        List<string> advisoryKeys;
        lock (state)
        {
            if (state.Completed || state.Perception is null)
            {
                return;
            }

            if (!forced && !_options.BundleRoster.All(state.Advisories.ContainsKey))
            {
                return;
            }

            state.Completed = true;
            perception = state.Perception;
            advisorySnapshot = [.. state.Advisories.Values];
            advisoryKeys = [.. state.Advisories.Keys];
        }

        state.TimeoutCts.Cancel();

        if (forced)
        {
            var missing = _options.BundleRoster.Except(advisoryKeys).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning("Governance bundle {CorrelationId} completed on timeout, missing advisors: {Missing}",
                    perception.CorrelationId, string.Join(", ", missing));
            }
        }

        var severity = SeverityExtensions.MaxOf(advisorySnapshot.Select(a => a.Severity).Append(perception.Severity));
        var meta = BuildBundleMeta(perception, advisorySnapshot);
        var bundle = perception.Derive(Topics.Bundle, Name, severity, meta);
        _bus.Publish(Topics.Bundle, bundle);
    }

    /// <summary>
    /// Folds each advisory's own meta into the perceived event's meta so
    /// content (not just severity) reaches Intent — reused for the initial
    /// bundle and for re-issuing one on a revision pass. Takes a snapshot
    /// rather than the live BundleState.Advisories dictionary, since callers
    /// enumerate outside the lock that guards concurrent writers.
    /// </summary>
    private static MetaBag BuildBundleMeta(Envelope perception, IReadOnlyList<Envelope> advisories) =>
        advisories.Aggregate(perception.Meta, (acc, advisory) => acc.Merge(advisory.Meta));

    private async Task OnVerdictAsync(Envelope verdict, CancellationToken cancellationToken)
    {
        var value = verdict.Meta.Get<Verdict>(SecurityAgent.VerdictKey);
        var reply = verdict.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
        var isReflex = verdict.Meta.Get<bool>(ImpulseAgent.ReflexKey);

        // GetOrAdd: a reflex verdict can outrace Governance's own
        // Perception-topic loop, same reason OnAdvisory needs it.
        var state = _bundles.GetOrAdd(verdict.CorrelationId, _ => new BundleState());

        if (value == Verdict.Yellow && !isReflex)
        {
            Envelope? revisionBundle = null;
            lock (state)
            {
                if (state.RevisionCount < _options.MaxRevisionPasses)
                {
                    state.RevisionCount++;
                    var concern = verdict.Meta.Get<string>(SecurityAgent.ConcernKey) ?? string.Empty;
                    var revisionMeta = BuildBundleMeta(state.Perception!, [.. state.Advisories.Values]).With(RevisionConcernKey, concern);
                    revisionBundle = state.Perception!.Derive(Topics.Bundle, Name, verdict.Severity, revisionMeta);
                }
            }

            if (revisionBundle is not null)
            {
                // Not concluded: this Yellow bought exactly one Intent
                // revision pass. A second Yellow on the revised proposal
                // falls through below and proceeds to Action regardless —
                // blocking on mere ambiguity would make every unresolved
                // judgment call a hard stop, which is Red's job, not Yellow's.
                _bus.Publish(Topics.Bundle, revisionBundle);
                return;
            }
        }

        var replyToSpeak = value == Verdict.Red ? BlockedReply(verdict) : reply;
        var prompt = verdict.Meta.Get<string>(IntentAgent.PromptKey) ?? string.Empty;
        var actionMeta = MetaBag.Empty
            .With(IntentAgent.ReplyKey, replyToSpeak)
            .With(IntentAgent.PromptKey, prompt)
            .With(SecurityAgent.VerdictKey, value);
        if (value == Verdict.Red)
        {
            actionMeta = await AppendFrustrationAsync(verdict, actionMeta, cancellationToken).ConfigureAwait(false);
        }

        var action = verdict.Derive(Topics.Action, Name, verdict.Severity, actionMeta);
        _bus.Publish(Topics.Action, action);

        if (isReflex)
        {
            // The reflex reaction reached the human, but the event is not
            // over: Intent's considered reply still follows behind it, and
            // THAT is what concludes the event — see plan §3.5.
            return;
        }

        var conclusion = verdict.Derive(Topics.Conclusion, Name, verdict.Severity, actionMeta);
        _bus.Publish(Topics.Conclusion, conclusion);

        _bundles.TryRemove(verdict.CorrelationId, out _);
    }

    private static string BlockedReply(Envelope verdict)
    {
        var concern = verdict.Meta.Get<string>(SecurityAgent.ConcernKey);
        return string.IsNullOrEmpty(concern)
            ? "I can't help with that."
            : $"I can't help with that: {concern}";
    }

    /// <summary>
    /// A Red verdict is the actual block path: nudge Impulse's drive state
    /// via system.control (never a direct call — same loose-coupling
    /// discipline ReflectionAgent's push-vs-write gate follows), attach the
    /// resulting expression to the blocked Action/Conclusion, and write a
    /// durable cold-storage record so the alert is queryable later.
    /// </summary>
    private async Task<MetaBag> AppendFrustrationAsync(Envelope verdict, MetaBag meta, CancellationToken cancellationToken)
    {
        var records = await _store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
        var vectors = records.Count > 0
            ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors()
            : new DriveVectors();
        var expression = vectors.Expression();

        var control = Envelope.Create(Topics.SystemControl, Name, Severity.Elevated,
            MetaBag.Empty.With(ConsolidatorAgent.ControlKindKey, FrustrationKind));
        _bus.Publish(Topics.SystemControl, control);

        var concern = verdict.Meta.Get<string>(SecurityAgent.ConcernKey) ?? "blocked";
        var alertRecord = new AgentStateRecord(SecurityAlertPathAnchor, $"{expression}: {concern}", DateTimeOffset.UtcNow, ArchiveDomain.Internal);
        await _store.WriteAsync([alertRecord], cancellationToken).ConfigureAwait(false);

        return meta.With(ExpressionKey, expression).With(SecurityAlertKey, true);
    }

    private sealed class BundleState
    {
        public Envelope? Perception { get; set; }
        public Dictionary<string, Envelope> Advisories { get; } = [];
        public bool Completed { get; set; }
        public int RevisionCount { get; set; }
        public CancellationTokenSource TimeoutCts { get; } = new();
    }
}
