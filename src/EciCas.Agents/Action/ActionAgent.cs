using EciCas.Agents.Intent;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Action;

/// <summary>
/// Terminal agent. Publishes nothing — "voicing" the answer is a side effect
/// the Host's own subscriber (ConsoleSubscriber) picks up off events.action,
/// same as ArchiveLogger does. Action itself never knows a console exists.
/// </summary>
public sealed class ActionAgent : AgentBase
{
    public ActionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ActionAgent> logger)
        : base(bus, activity, logger)
    {
    }

    public override string Name => "Action";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Action];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
