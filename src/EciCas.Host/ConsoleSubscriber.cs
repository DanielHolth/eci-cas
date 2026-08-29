using EciCas.Agents.Intent;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Host;

/// <summary>
/// An ordinary subscriber, not a display hook baked into any agent. Prints
/// the voiced answer when it sees events.action; every other envelope is a
/// one-line trace. No agent knows this exists.
/// </summary>
public sealed class ConsoleSubscriber : AgentBase
{
    public ConsoleSubscriber(IMessageBus bus, BusActivityTracker activity, ILogger<ConsoleSubscriber> logger)
        : base(bus, activity, logger)
    {
    }

    public override string Name => "ConsoleSubscriber";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Topic == Topics.Action)
        {
            var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
            Console.WriteLine($"> {reply}");
        }
        else
        {
            Console.WriteLine($"  [{envelope.Topic}] {envelope.PublishedBy} ({envelope.Severity})");
        }

        return Task.CompletedTask;
    }
}
