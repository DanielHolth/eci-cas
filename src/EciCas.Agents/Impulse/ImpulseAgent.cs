using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Impulse;

/// <summary>
/// Fast deterministic appraisal, plus the Critical reflex: a second publisher
/// on events.proposal alongside Intent, so Security's gate and Governance's
/// verdict handling need no reflex-specific branch — see plan §3.5. The
/// "don't double-conclude a reflex + considered reply" refinement is M3
/// (gating matrix) work and is not attempted here.
/// </summary>
public sealed class ImpulseAgent : AgentBase
{
    public const string AdviceKey = "impulse.advice";

    private static readonly string[] CriticalTriggers = ["help", "emergency", "urgent"];

    private readonly IMessageBus _bus;

    public ImpulseAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ImpulseAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Impulse";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
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
            var proposal = envelope.Derive(Topics.Proposal, Name, severity, MetaBag.Empty.With(IntentAgent.ReplyKey, reply));
            _bus.Publish(Topics.Proposal, proposal);
        }

        return Task.CompletedTask;
    }
}
