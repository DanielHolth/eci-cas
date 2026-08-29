using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Self;

/// <summary>
/// Identity lookup. M2: a fixed snippet — IArchiveStore (M4) doesn't exist
/// yet, so there is nothing to read. Becomes a thin adapter over stored
/// persona/identity records once M4 lands; deterministic either way, so no
/// substrate call and no CognitiveAgent&lt;T&gt; base.
/// </summary>
public sealed class SelfAgent : AgentBase
{
    public const string AdviceKey = "self.advice";

    private const string IdentitySnippet = "I'm ECI, here to help.";

    private readonly IMessageBus _bus;

    public SelfAgent(IMessageBus bus, BusActivityTracker activity, ILogger<SelfAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Self";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(AdviceKey, IdentitySnippet));
        _bus.Publish(Topics.Advisories, advisory);
        return Task.CompletedTask;
    }
}
