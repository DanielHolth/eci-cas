using EciCas.Agents.Intent;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Security;

/// <summary>
/// M1 stub: always green. The real deterministic rule engine (green/yellow/red
/// gating) lands in M3 — see plan §5. The gate exists here from the first
/// commit so events.action can never be wired without it in front.
/// </summary>
public sealed class SecurityAgent : AgentBase
{
    public const string VerdictKey = "security.verdict";

    private readonly IMessageBus _bus;

    public SecurityAgent(IMessageBus bus, BusActivityTracker activity, ILogger<SecurityAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Security";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Proposal];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
        var meta = MetaBag.Empty.With(VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, reply);

        var verdict = envelope.Derive(Topics.Verdict, Name, envelope.Severity, meta);
        _bus.Publish(Topics.Verdict, verdict);
        return Task.CompletedTask;
    }
}
