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

    /// <summary>
    /// Which person this input came from, opaque to every agent that reads
    /// it — a profile id from the surface, absent on input that no profile
    /// owns (the console loop, Reflection's self-generated ideas). Impulse
    /// keys its drive state on it so the persona holds a separate emotional
    /// relationship with each person; Governance carries it onto the
    /// frustration signal for the same reason.
    /// </summary>
    public const string ProfileKey = "perception.profile";

    private readonly IMessageBus _bus;

    public PerceptionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<PerceptionAgent> logger)
        : base(bus, activity, logger) => _bus = bus;

    public override string Name => "Perception";
    public override IReadOnlyCollection<string> Subscriptions => [];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Perceive(string text, string? profileId = null)
    {
        var meta = MetaBag.Empty.With(TextKey, text);
        if (!string.IsNullOrEmpty(profileId))
        {
            meta = meta.With(ProfileKey, profileId);
        }

        var envelope = Envelope.Create(Topics.Perception, Name, Severity.Neutral, meta);
        _bus.Publish(Topics.Perception, envelope);
    }
}
