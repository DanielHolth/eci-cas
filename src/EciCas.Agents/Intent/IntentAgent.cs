using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Intent;

/// <summary>
/// M1: mock substrate — echoes the perceived text back as a canned reply.
/// Real substrate call (CognitiveAgent&lt;T&gt;) lands in M2.
/// </summary>
public sealed class IntentAgent : AgentBase
{
    public const string ReplyKey = "intent.reply";

    private readonly IMessageBus _bus;

    public IntentAgent(IMessageBus bus, BusActivityTracker activity, ILogger<IntentAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Intent";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var reply = $"I heard: {text}";

        var proposal = envelope.Derive(Topics.Proposal, Name, envelope.Severity, MetaBag.Empty.With(ReplyKey, reply));
        _bus.Publish(Topics.Proposal, proposal);
        return Task.CompletedTask;
    }
}
