using EciCas.Agents.Consolidator;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Reflection;

/// <summary>
/// Publishes self-generated ideas back onto events.perception — downstream
/// nothing knows the difference from external input (plan §3.6). Guarded by
/// Generation so an idea -> arc -> conclusion -> idea chain can't loop
/// forever spending on LLM calls; capped at ReflectionOptions.MaxIdeaGeneration.
/// </summary>
public sealed class ReflectionAgent : CognitiveAgent<string>
{
    public const string TriggeredByKey = "perception.triggered_by";
    public const string SourceTypeKey = "perception.source_type";
    public const string ReflectedKind = "Reflected";

    private readonly IMessageBus _bus;
    private readonly ReflectionOptions _options;

    public ReflectionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ReflectionAgent> logger, ISubstrateProvider substrate, IOptions<ReflectionOptions> options)
        : base(bus, activity, logger, substrate)
    {
        _bus = bus;
        _options = options.Value;
    }

    public override string Name => "Reflection";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Conclusion];

    protected override string SubstrateClass => "slow-low";
    protected override FallbackPosture Fallback => FallbackPosture.Closed;

    protected override string BuildPrompt(Envelope envelope)
    {
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
        return $"In one short sentence, note a follow-up thought or question worth exploring later, prompted by having just said: {reply}";
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => string.Empty;

    protected override void Publish(Envelope envelope, string result, SubstrateResult? diagnostics)
    {
        var reflected = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ConsolidatorAgent.ControlKindKey, ReflectedKind));
        _bus.Publish(Topics.SystemControl, reflected);

        if (envelope.Generation >= _options.MaxIdeaGeneration)
        {
            return;
        }

        // A new arc, not a continuation: this idea starts its own correlation
        // (Envelope.Create), one generation higher than the conclusion that
        // prompted it — the loop guard the generation cap enforces above.
        var idea = Envelope.Create(Topics.Perception, Name, Severity.Restful,
            MetaBag.Empty.With(PerceptionAgent.TextKey, result).With(TriggeredByKey, "self").With(SourceTypeKey, "idea"),
            generation: envelope.Generation + 1);
        _bus.Publish(Topics.Perception, idea);
    }
}
