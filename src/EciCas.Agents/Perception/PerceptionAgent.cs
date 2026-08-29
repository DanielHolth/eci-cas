using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Perception;

/// <summary>
/// Entry point for external input (console, webhook, sensor — anything).
/// No bus subscriptions; the host calls PerceiveAsync directly and this agent
/// turns that into the first envelope of a turn.
/// </summary>
public sealed class PerceptionAgent : AgentBase
{
    public const string TextKey = "perception.text";

    private readonly IMessageBus _bus;

    public PerceptionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<PerceptionAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Perception";
    public override IReadOnlyCollection<string> Subscriptions => [];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Perceive(string text)
    {
        var envelope = Envelope.Create(Topics.Perception, Name, Severity.Neutral, MetaBag.Empty.With(TextKey, text));
        _bus.Publish(Topics.Perception, envelope);
    }
}
