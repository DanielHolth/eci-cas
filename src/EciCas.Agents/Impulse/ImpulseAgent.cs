using System.Text.Json;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Impulse;

/// <summary>
/// Fast deterministic appraisal, plus the Critical reflex: a second publisher
/// on events.proposal alongside Intent, so Security's gate needs no
/// reflex-specific branch — see plan §3.5. The reflex proposal carries
/// <see cref="ReflexKey"/> so Governance can tell it apart from Intent's
/// considered proposal and avoid double-concluding the event (M3).
///
/// Also owns the persona drive-vector state (see DriveVectors) that
/// ReflectionAgent's push-vs-write gate reads — ported from the Python
/// prototype's Impulse, minimally: nudged only from the isCritical signal
/// already computed here, not the full drift/appraisal-axis machinery (see
/// gap-analysis.md, that stays a separate follow-up). Persisted through
/// IArchiveStore under DrivePath, same pattern SelfAgent uses for identity —
/// no direct reference from Reflection to this agent, preserving loose
/// coupling.
/// </summary>
public sealed class ImpulseAgent : AgentBase
{
    public const string AdviceKey = "impulse.advice";

    /// <summary>Set on the reflex's own proposal. Absent (default false) on Intent's considered proposal.</summary>
    public const string ReflexKey = "impulse.reflex";

    /// <summary>Archive path holding the current DriveVectors, JSON-serialized. Read directly by ReflectionAgent.</summary>
    public const string DrivePath = "impulse/drive";

    private static readonly string[] CriticalTriggers = ["help", "emergency", "urgent"];

    /// <summary>Fixed, named nudge applied on a critical event — same discipline as Python's FRUSTRATION_NUDGE: something may ask for a shift, but the number that lands is written here, in code.</summary>
    private static readonly DriveVectors CriticalNudge = new(Curiosity: -0.05, Fatigue: 0.05, Urgency: 0.15, SocialDrive: 0, Temperature: -0.05);

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private DriveVectors? _cached;

    public ImpulseAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ImpulseAgent> logger, IArchiveStore store)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
    }

    public override string Name => "Impulse";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var isCritical = CriticalTriggers.Any(trigger => text.Contains(trigger, StringComparison.OrdinalIgnoreCase));

        // Reflex severity is capped at Elevated — only Perception/Reasoning may tag Critical.
        var severity = isCritical ? Severity.Elevated : envelope.Severity;
        var advice = isCritical ? "flagged as urgent" : "no immediate concern";

        var advisory = envelope.Derive(Topics.Advisories, Name, severity, MetaBag.Empty.With(AdviceKey, advice));
        _bus.Publish(Topics.Advisories, advisory);

        if (isCritical)
        {
            var reply = "This sounds urgent — I'm on it right away.";
            var proposal = envelope.Derive(Topics.Proposal, Name, severity,
                MetaBag.Empty.With(IntentAgent.ReplyKey, reply).With(ReflexKey, true));
            _bus.Publish(Topics.Proposal, proposal);

            await NudgeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NudgeAsync(CancellationToken cancellationToken)
    {
        var current = await GetVectorsAsync(cancellationToken).ConfigureAwait(false);
        var updated = current.Add(CriticalNudge);

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = updated;
        }
        finally
        {
            _cacheLock.Release();
        }

        var record = new ArchiveRecord(DrivePath, JsonSerializer.Serialize(updated), DateTimeOffset.UtcNow, ArchiveDomain.Internal);
        await _store.WriteAsync([record], cancellationToken).ConfigureAwait(false);
    }

    private async Task<DriveVectors> GetVectorsAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } cachedAfterLock)
            {
                return cachedAfterLock;
            }

            var records = await _store.LookupAsync([DrivePath], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
            _cached = records.Count > 0 ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors() : new DriveVectors();
            return _cached;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
