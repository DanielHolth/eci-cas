using EciCas.Agents.Intent;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Security;

/// <summary>
/// The real gate: a deterministic, declarative rule engine (SecurityRuleSet),
/// no model. Security sees only the proposed reply text and nothing else —
/// not Reasoning's or Self's advice, not Intent's diagnostics. A rule engine
/// that could see the argument FOR a reply would be evaluating the argument,
/// which is Intent's job, not the rules'.
/// </summary>
public sealed class SecurityAgent : AgentBase
{
    public const string VerdictKey = "security.verdict";
    public const string ConcernKey = "security.concern";

    private readonly IMessageBus _bus;
    private readonly SecurityRuleSet _rules;

    public SecurityAgent(IMessageBus bus, BusActivityTracker activity, ILogger<SecurityAgent> logger, SecurityRuleSet rules)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _rules = rules;
    }

    public override string Name => "Security";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Proposal];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
        var evaluation = _rules.Evaluate(reply);

        var overlay = MetaBag.Empty.With(VerdictKey, evaluation.Verdict).With(IntentAgent.ReplyKey, reply);
        if (evaluation.Verdict != Verdict.Green)
        {
            overlay = overlay.With(ConcernKey, evaluation.Concern);
        }

        // Merge onto the incoming proposal's meta (not replace it) so
        // upstream markers — e.g. Impulse's reflex flag — survive the hop
        // to Governance. The evaluation's own keys win.
        var meta = envelope.Meta.Merge(overlay);

        var verdict = envelope.Derive(Topics.Verdict, Name, envelope.Severity, meta);
        _bus.Publish(Topics.Verdict, verdict);
        return Task.CompletedTask;
    }
}
