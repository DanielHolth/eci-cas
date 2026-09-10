using EciCas.Agents.Reflection;
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
    private readonly RuntimeKnobs _knobs;

    public PerceptionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<PerceptionAgent> logger, RuntimeKnobs knobs)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _knobs = knobs;
    }

    public override string Name => "Perception";
    public override IReadOnlyCollection<string> Subscriptions => [];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Turns one person's input into the turn's first envelope, bounded once
    /// here at <see cref="RuntimeKnobs.PerceptionChars"/> and never again.
    ///
    /// Once, and at the boundary, because provenance is already structural:
    /// everything that reaches this method was typed by a person, and the
    /// only other producer of a perception envelope -- Reflection's own idea
    /// -- publishes its own and carries PromptCap's tighter loop guard
    /// instead. So the readers downstream can take this text as given, which
    /// is what stopped five of them re-truncating a person's paste to 240
    /// characters apiece.
    ///
    /// <paramref name="self"/> marks the text as the persona's own thought
    /// rather than a person's words. It is the same flag Reflection stamps,
    /// and it is here for the one caller that has a thought to hand without
    /// having run a batch to find it: the surface nudging the persona with a
    /// note it already wrote. Hindsight wakes on it (HindsightAgent's first
    /// trigger) and the display layer draws it as an idea, not an utterance.
    /// </summary>
    public void Perceive(string text, string? profileId = null, bool self = false)
    {
        var meta = MetaBag.Empty.With(TextKey, PromptCap.Apply(text, _knobs.PerceptionChars));
        if (!string.IsNullOrEmpty(profileId))
        {
            meta = meta.With(ProfileKey, profileId);
        }

        if (self)
        {
            meta = meta.With(ReflectionAgent.TriggeredByKey, "self");
        }

        var envelope = Envelope.Create(Topics.Perception, Name, Severity.Neutral, meta);
        _bus.Publish(Topics.Perception, envelope);
    }
}
