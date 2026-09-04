using System.Collections.Concurrent;
using System.Text.Json;
using EciCas.Agents.Archivist;
using EciCas.Agents.Impulse;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Governance;

/// <summary>
/// Decision-only: bundles the advisory fan-out, gates Action on Security's
/// verdict (green/yellow/red per Security's matrix), and produces the
/// conclusion. Nothing else.
/// </summary>
public sealed class GovernanceAgent : AgentBase
{
    /// <summary>Carries Security's concern into a re-issued Bundle so Intent can revise. Set only on a revision pass.</summary>
    public const string RevisionConcernKey = "governance.revision_concern";

    /// <summary>
    /// system.control kind published on a Red verdict — mirrors
    /// ArchivistAgent.WrittenKind/ReflectionAgent.ReflectedKind's
    /// convention. ImpulseAgent listens for this to apply FrustrationNudge,
    /// with no direct reference between the two agents.
    /// </summary>
    public const string FrustrationKind = "Frustration";

    /// <summary>
    /// The face the persona's drive state implies, attached to every
    /// Action/Conclusion so what reaches the human matches how it feels. On
    /// an ordinary turn it is Impulse's own appraisal, forwarded; on a block
    /// it is re-read after the frustration nudge, which is the whole point of
    /// nudging.
    /// </summary>
    public const string ExpressionKey = "governance.expression";

    /// <summary>Impulse's roster name, as it appears on its advisory's PublishedBy — a string here rather than a type reference, same as every other roster entry.</summary>
    private const string ImpulseAdvisor = "Impulse";

    /// <summary>Set true on a blocked Action/Conclusion so downstream consumers can tell a security block from an ordinary reply without inspecting VerdictKey.</summary>
    public const string SecurityAlertKey = "governance.security_alert";

    /// <summary>Set on an Action/Conclusion whose reply carries a degraded-substrate notice, so the surface can mark it as one rather than parsing the text.</summary>
    public const string DegradedKey = "governance.degraded";

    private const string SecurityAlertPathAnchor = "security_alert";

    private readonly IMessageBus _bus;
    private readonly ILogger<GovernanceAgent> _logger;
    private readonly GovernanceOptions _options;
    private readonly IAgentStateStore _store;
    private readonly IInstructionStore _instructions;
    private readonly ConcurrentDictionary<Guid, BundleState> _bundles = new();

    public GovernanceAgent(IMessageBus bus, BusActivityTracker activity, ILogger<GovernanceAgent> logger, IOptions<GovernanceOptions> options, IAgentStateStore store,
        IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _logger = logger;
        _options = options.Value;
        _store = store;
        _instructions = instructions;
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
            _ = RunTimeoutAsync(perception.CorrelationId, state);
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

    /// <summary>
    /// The one timer a bundle gets, doing both jobs in sequence: complete it
    /// when the advisors have had long enough, then let it go if the verdict
    /// never comes back.
    ///
    /// There is no cancellation here, and that is the point. This used to hold
    /// a CancellationTokenSource so an early completion could end the wait a
    /// few seconds sooner — a source nobody disposed, one per turn, in a
    /// process meant to run for years beside one person. An uncancelled delay
    /// costs a pending timer that clears itself, and both calls below are
    /// no-ops once the bundle has moved on. Cheaper to run and cheaper to
    /// reason about than a resource with no owner.
    /// </summary>
    private async Task RunTimeoutAsync(Guid correlationId, BundleState state)
    {
        await Task.Delay(_options.BundleTimeoutMs).ConfigureAwait(false);
        TryComplete(state, forced: true);

        await Task.Delay(_options.BundleAbandonMs).ConfigureAwait(false);

        // Reached only when no verdict ever arrived — OnVerdictAsync retires
        // its own bundle. Before this, such a turn stayed in the dictionary
        // for good, and a persona that runs for years accumulates every turn
        // that ever broke.
        if (_bundles.TryRemove(correlationId, out _))
        {
            _logger.LogWarning("Governance bundle {CorrelationId} abandoned after {Ms} ms with no verdict",
                correlationId, _options.BundleAbandonMs);
        }
    }

    private void TryComplete(BundleState state, bool forced = false)
    {
        Envelope perception;
        List<Envelope> advisorySnapshot;
        List<string> advisoryKeys;
        string[] impaired;
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

            // Governance is the only agent that can know this: it bundles the
            // fan-out by CorrelationId, so it alone sees which advisories
            // arrived, which arrived degraded, and which never came at all.
            // Every other agent knows only its own fate.
            //
            // Absent counts the same as degraded on purpose — from the
            // person's side, an advisor that timed out and one that answered
            // with nothing are the same missing faculty.
            impaired = [.. _options.BundleRoster.Where(advisor =>
                !state.Advisories.TryGetValue(advisor, out var advisory)
                || advisory.Meta.Get<string>(SubstrateHealth.DegradedKey) is not null)];
            state.Impaired = impaired;

            // Same reason Impaired is captured here: the verdict envelope
            // never carried the advisories, and by the time the action is
            // built this is the only place Impulse's face still exists.
            state.Expression = state.Advisories.TryGetValue(ImpulseAdvisor, out var impulse)
                ? impulse.Meta.Get<string>(ImpulseAgent.ExpressionKey)
                : null;
        }

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
                // Perception can be null here: GetOrAdd above will have minted
                // an empty state for a verdict whose bundle was already
                // retired, and a revision needs the originating envelope to
                // derive from. Falling through proceeds to Action, which is
                // where a second verdict belongs anyway.
                if (state.Perception is not null && state.RevisionCount < _options.MaxRevisionPasses)
                {
                    state.RevisionCount++;
                    var concern = verdict.Meta.Get<string>(SecurityAgent.ConcernKey) ?? string.Empty;
                    var revisionMeta = BuildBundleMeta(state.Perception, [.. state.Advisories.Values]).With(RevisionConcernKey, concern);
                    revisionBundle = state.Perception.Derive(Topics.Bundle, Name, verdict.Severity, revisionMeta);
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

        // Intent's own failure is read off the verdict — Security merges the
        // proposal's meta forward rather than replacing it, so the marker
        // survives the hop.
        var intentDegraded = verdict.Meta.Get<string>(SubstrateHealth.DegradedKey);
        string[] impaired;
        string? expression;
        lock (state)
        {
            impaired = state.Impaired;
            expression = state.Expression;
        }

        var replyToSpeak = value == Verdict.Red ? BlockedReply(verdict) : reply;
        var notice = Notice(intentDegraded, impaired);
        if (notice is not null && value != Verdict.Red)
        {
            // Not on a block: a refusal is already an honest answer about
            // what the persona will do, and qualifying it with how well it
            // was thinking would only muddy it.
            replyToSpeak = intentDegraded is not null ? notice : replyToSpeak + Environment.NewLine + Environment.NewLine + notice;
        }

        var context = verdict.Meta.Get<string>(IntentAgent.ContextKey) ?? string.Empty;
        var actionMeta = MetaBag.Empty
            .With(IntentAgent.ReplyKey, replyToSpeak)
            .With(IntentAgent.ContextKey, context)
            .With(SecurityAgent.VerdictKey, value);

        // Second and last hop for the note lineage: Reflection reads it off
        // the conclusion, and this bag is built fresh rather than inherited.
        // Same forwarding ContextKey needs, for the same reason.
        if (verdict.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NoteIdsKey) is { Count: > 0 } noteIds)
        {
            actionMeta = actionMeta
                .With(HindsightAgent.NoteIdsKey, noteIds)
                .With(HindsightAgent.EchoDepthKey, verdict.Meta.Get<int>(HindsightAgent.EchoDepthKey));
        }
        if (notice is not null && value != Verdict.Red)
        {
            actionMeta = actionMeta.With(DegradedKey, true);
        }

        if (expression is not null)
        {
            actionMeta = actionMeta.With(ExpressionKey, expression);
        }

        if (value == Verdict.Red)
        {
            // The verdict envelope carries no profile — Derive() replaces
            // meta rather than inheriting it — so it comes off the bundled
            // perception, which is the only envelope that ever held it.
            var profileId = state.Perception?.Meta.Get<string>(PerceptionAgent.ProfileKey);
            actionMeta = await AppendFrustrationAsync(verdict, actionMeta, profileId, cancellationToken).ConfigureAwait(false);
        }

        var action = verdict.Derive(Topics.Action, Name, verdict.Severity, actionMeta);
        _bus.Publish(Topics.Action, action);

        if (isReflex)
        {
            // The reflex reaction reached the human, but the event is not
            // over: Intent's considered reply still follows behind it, and
            // THAT is what concludes the event.
            return;
        }

        var conclusion = verdict.Derive(Topics.Conclusion, Name, verdict.Severity, actionMeta);
        _bus.Publish(Topics.Conclusion, conclusion);

        _bundles.TryRemove(verdict.CorrelationId, out _);
    }

    /// <summary>
    /// Deterministic native text, for the same reason the block text above is:
    /// an LLM-authored apology cannot be produced by an LLM that isn't
    /// answering. This is the crux, not a style preference.
    ///
    /// Two cases, and they are genuinely different. If Intent itself failed
    /// there is no reply — only a fallback sentence that sounds like one — so
    /// the notice replaces it. If Intent succeeded but its advisors didn't,
    /// there is a real reply that is simply less grounded than it looks, and
    /// the notice qualifies it rather than discarding it. That second case is
    /// the dangerous one this whole mechanism exists for: fluent, confident
    /// and ungrounded reads exactly like fluent, confident and grounded.
    /// </summary>
    private string? Notice(string? intentDegraded, IReadOnlyList<string> impaired)
    {
        if (intentDegraded is not null)
        {
            return InstructionFile.Fill(_instructions.For(Name, "reasoning-down"), ("cause", intentDegraded));
        }

        return impaired.Count == 0
            ? null
            : InstructionFile.Fill(_instructions.For(Name, "less-grounded"), ("impaired", string.Join(" and ", impaired)));
    }

    private string BlockedReply(Envelope verdict)
    {
        var concern = verdict.Meta.Get<string>(SecurityAgent.ConcernKey);
        return string.IsNullOrEmpty(concern)
            ? _instructions.For(Name, "blocked")
            : InstructionFile.Fill(_instructions.For(Name, "blocked-with-reason"), ("concern", concern));
    }

    /// <summary>
    /// A Red verdict is the actual block path: nudge Impulse's drive state
    /// via system.control (never a direct call — same loose-coupling
    /// discipline ReflectionAgent's push-vs-write gate follows), attach the
    /// resulting expression to the blocked Action/Conclusion, and write a
    /// durable cold-storage record so the alert is queryable later.
    /// </summary>
    private async Task<MetaBag> AppendFrustrationAsync(Envelope verdict, MetaBag meta, string? profileId, CancellationToken cancellationToken)
    {
        var records = await _store.LookupAsync([ImpulseAgent.DrivePathFor(profileId)], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
        var vectors = records.Count > 0
            ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors()
            : new DriveVectors();
        var expression = vectors.Expression();

        var controlMeta = MetaBag.Empty.With(ArchivistAgent.ControlKindKey, FrustrationKind);
        if (!string.IsNullOrEmpty(profileId))
        {
            controlMeta = controlMeta.With(PerceptionAgent.ProfileKey, profileId);
        }

        var control = Envelope.Create(Topics.SystemControl, Name, Severity.Elevated, controlMeta);
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

        /// <summary>Impulse's appraised face for this turn, read off its advisory when the bundle completes — the verdict never carried it.</summary>
        public string? Expression { get; set; }

        /// <summary>Roster advisors that failed or never arrived, decided once when the bundle completes and read again when the verdict comes back — the verdict envelope never carried the advisories.</summary>
        public string[] Impaired { get; set; } = [];
        public bool Completed { get; set; }
        public int RevisionCount { get; set; }
    }
}
