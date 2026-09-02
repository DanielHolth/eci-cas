using System.Text.Json;
using EciCas.Agents.Consolidator;
using EciCas.Agents.Governance;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
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
/// IArchiveStore under DrivePath, same pattern IdentityAgent uses for identity —
/// no direct reference from Reflection to this agent, preserving loose
/// coupling.
/// </summary>
public sealed class ImpulseAgent : AgentBase
{
    public const string AdviceKey = "impulse.advice";

    /// <summary>Set on the reflex's own proposal. Absent (default false) on Intent's considered proposal.</summary>
    public const string ReflexKey = "impulse.reflex";

    /// <summary>
    /// The face the drive state implies once this turn's nudges have landed
    /// — DriveVectors.Expression(), published on the advisory so Governance
    /// can carry it out to whatever surface is watching. Appraisal is
    /// Impulse's own data, not a display concern: the surface decides how to
    /// draw a word it did not choose.
    /// </summary>
    public const string ExpressionKey = "impulse.expression";

    /// <summary>
    /// Archive path holding the current DriveVectors, JSON-serialized. Read
    /// directly by ReflectionAgent and GovernanceAgent. This is the
    /// device-wide state — the path used when no profile owns the input.
    /// </summary>
    public const string DrivePath = "impulse/drive";

    /// <summary>
    /// Drive state is per profile: the persona holds a separate emotional
    /// relationship with each person, so what warms it toward one child does
    /// not pre-colour how it meets the parent an hour later. That is a
    /// keying change, not a redesign — one state record per profile under
    /// the same path prefix, and <see cref="DrivePath"/> when the input
    /// belongs to nobody in particular (the console loop, a self-generated
    /// idea), which is also what a single-user install keeps using.
    /// </summary>
    public static string DrivePathFor(string? profileId) =>
        string.IsNullOrEmpty(profileId) ? DrivePath : $"{DrivePath}/{profileId}";

    private static readonly string[] CriticalTriggers = ["help", "emergency", "urgent"];

    /// <summary>
    /// §5.4's Somatic shortcut, scoped down: Python ties this to a physical
    /// Sensory input tagging approval/disapproval, with Impulse shifting
    /// instantly and Intent reviewing alignment retroactively. No physical
    /// sensor channel exists here, and there's no case for porting one just
    /// for this — a keyword flag on the perceived text, same discipline as
    /// CriticalTriggers above, gives the same "instant shift, no Intent
    /// pre-approval" shape without a state machine.
    /// </summary>
    private static readonly string[] PositiveTriggers = ["thanks", "thank you", "great job", "well done", "awesome"];
    private static readonly string[] NegativeTriggers = ["that's wrong", "that's not right", "terrible", "bad job"];

    /// <summary>
    /// Fixed, named nudge applied on a critical event — same discipline as
    /// Python's FRUSTRATION_NUDGE: something may ask for a shift, but the
    /// number that lands is written here, in code.
    ///
    /// Sized against DriveVectors' bucket edges, not against the Python
    /// prototype's numbers: a nudge called instant that leaves the appraised
    /// face unchanged is not instant. One emergency crosses into "alert" on
    /// its own; two thank-yous reach "warm"; sustained disapproval walks
    /// engagement down into "sad". Slow colouring stays an order of
    /// magnitude below all of it, which is the invariant ImpulseAgentTests
    /// guards.
    /// </summary>
    private static readonly DriveVectors CriticalNudge = new(Curiosity: -0.05, Fatigue: 0.05, Urgency: 0.45, SocialDrive: 0, Temperature: -0.05);

    /// <summary>
    /// Ported verbatim from Python's FRUSTRATION_NUDGE (agents/impulse/agent.py):
    /// more urgency (this mattered and it didn't work), a little more fatigue
    /// (it cost something), slightly less warmth — applied when Governance
    /// signals a blocked exchange over system.control, never a direct call.
    /// </summary>
    private static readonly DriveVectors FrustrationNudge = new(Curiosity: 0, Fatigue: 0.05, Urgency: 0.3, SocialDrive: 0, Temperature: -0.1);

    /// <summary>Direct approval, applied instantly — warmer and less fatigued, no urgency change.</summary>
    private static readonly DriveVectors PositiveNudge = new(Curiosity: 0.05, Fatigue: -0.05, Urgency: 0, SocialDrive: 0.2, Temperature: 0.2);

    /// <summary>Direct disapproval, applied instantly — cooler and a little more fatigued, smaller than a security block.</summary>
    private static readonly DriveVectors NegativeNudge = new(Curiosity: -0.1, Fatigue: 0.05, Urgency: 0, SocialDrive: -0.1, Temperature: -0.15);

    /// <summary>
    /// §5.3 slow-coloring feedback: drive state drifting with the tone of
    /// what's been happening, as opposed to the instant keyword-triggered
    /// shifts above. Reflection reports a mood LABEL for a whole batch of
    /// concluded turns and the delta it maps to is written here, in code —
    /// same discipline as FrustrationNudge, which is also requested
    /// elsewhere and quantified here.
    ///
    /// Every value is deliberately far smaller than any instant nudge, and
    /// fires once per ReflectionOptions.BatchSize turns rather than per
    /// turn. That gap IS the distinction between slow colouring and a
    /// somatic shortcut — raising these to instant-nudge magnitude would
    /// collapse the two into one mechanism. ImpulseAgentTests guards it.
    /// </summary>
    private static readonly Dictionary<string, DriveVectors> SlowColoring = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warm"] = new(Curiosity: 0.01, Fatigue: -0.01, Urgency: 0, SocialDrive: 0.02, Temperature: 0.02),
        ["tense"] = new(Curiosity: -0.01, Fatigue: 0.02, Urgency: 0.02, SocialDrive: -0.01, Temperature: -0.02),
        ["dull"] = new(Curiosity: -0.02, Fatigue: 0.02, Urgency: -0.01, SocialDrive: -0.01, Temperature: -0.01),
        ["curious"] = new(Curiosity: 0.03, Fatigue: -0.01, Urgency: 0, SocialDrive: 0.01, Temperature: 0.01),

        // Present, and zero, on purpose: "nothing stood out" is a real
        // answer Reflection can give, and an explicit no-op entry keeps it
        // distinguishable from a label nobody mapped.
        ["neutral"] = new(Curiosity: 0, Fatigue: 0, Urgency: 0, SocialDrive: 0, Temperature: 0),
    };

    /// <summary>Both exposed only so ImpulseAgentTests can assert every slow delta stays under every instant one, rather than pinning literals that are meant to be tuned.</summary>
    internal static IReadOnlyDictionary<string, DriveVectors> SlowColoringDeltas => SlowColoring;

    internal static IReadOnlyList<DriveVectors> InstantNudges => [CriticalNudge, FrustrationNudge, PositiveNudge, NegativeNudge];

    private readonly IMessageBus _bus;
    private readonly IAgentStateStore _store;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<string, DriveVectors> _cached = [];

    public ImpulseAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ImpulseAgent> logger, IAgentStateStore store)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
    }

    public override string Name => "Impulse";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.SystemControl];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Topic == Topics.SystemControl)
        {
            var kind = envelope.Meta.Get<string>(ConsolidatorAgent.ControlKindKey);
            var signalledProfile = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
            if (kind == GovernanceAgent.FrustrationKind)
            {
                await NudgeAsync(FrustrationNudge, signalledProfile, cancellationToken).ConfigureAwait(false);
            }
            else if (kind == ReflectionAgent.ReflectedKind
                && envelope.Meta.Get<string>(ReflectionAgent.MoodKey) is { } mood
                && SlowColoring.TryGetValue(mood, out var colouring))
            {
                // Slow colouring is still device-wide: Reflection scores a
                // whole batch of concluded turns in one substrate call, and
                // that batch can span profiles. Splitting it per profile is
                // a Reflection-side change, tracked in docs/roadmap.md.
                await NudgeAsync(colouring, signalledProfile, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var isCritical = CriticalTriggers.Any(trigger => text.Contains(trigger, StringComparison.OrdinalIgnoreCase));

        // Reflex severity is capped at Elevated — only Perception/Librarian may tag Critical.
        var severity = isCritical ? Severity.Elevated : envelope.Severity;
        var advice = isCritical ? "flagged as urgent" : "no immediate concern";

        if (isCritical)
        {
            var reply = "This sounds urgent — I'm on it right away.";
            var proposal = envelope.Derive(Topics.Proposal, Name, severity,
                MetaBag.Empty.With(IntentAgent.ReplyKey, reply).With(ReflexKey, true));
            _bus.Publish(Topics.Proposal, proposal);

            await NudgeAsync(CriticalNudge, profileId, cancellationToken).ConfigureAwait(false);
        }

        if (PositiveTriggers.Any(trigger => text.Contains(trigger, StringComparison.OrdinalIgnoreCase)))
        {
            await NudgeAsync(PositiveNudge, profileId, cancellationToken).ConfigureAwait(false);
        }
        else if (NegativeTriggers.Any(trigger => text.Contains(trigger, StringComparison.OrdinalIgnoreCase)))
        {
            await NudgeAsync(NegativeNudge, profileId, cancellationToken).ConfigureAwait(false);
        }

        // Advisory goes out last so the face it carries is the one this turn
        // just produced, not the one it inherited. The nudges above are cache
        // hits and a state write, not a substrate call, so the bundle waits
        // on nothing that matters.
        var vectors = await GetVectorsAsync(DrivePathFor(profileId), cancellationToken).ConfigureAwait(false);
        var advisory = envelope.Derive(Topics.Advisories, Name, severity,
            MetaBag.Empty.With(AdviceKey, advice).With(ExpressionKey, vectors.Expression()));
        _bus.Publish(Topics.Advisories, advisory);
    }

    private async Task NudgeAsync(DriveVectors nudge, string? profileId, CancellationToken cancellationToken)
    {
        var path = DrivePathFor(profileId);
        var current = await GetVectorsAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = current.Add(nudge);

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached[path] = updated;
        }
        finally
        {
            _cacheLock.Release();
        }

        var record = new AgentStateRecord(path, JsonSerializer.Serialize(updated), DateTimeOffset.UtcNow, ArchiveDomain.Internal);
        await _store.WriteAsync([record], cancellationToken).ConfigureAwait(false);
    }

    private async Task<DriveVectors> GetVectorsAsync(string path, CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var records = await _store.LookupAsync([path], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
        var vectors = records.Count > 0 ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors() : new DriveVectors();

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another nudge for the same profile may have landed while the
            // store read was in flight; that value is newer than this one.
            if (_cached.TryGetValue(path, out var raced))
            {
                return raced;
            }

            _cached[path] = vectors;
            return vectors;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
