using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Reasoning;

/// <summary>
/// Parametric-knowledge advisor: a substrate call over the perceived text,
/// nothing stored. Cognitive tier — see CognitiveAgent&lt;T&gt;. Recall (stored
/// records, M4) is a separate source of truth and is never merged into this.
/// </summary>
public sealed class ReasoningAgent : CognitiveAgent<string>
{
    public const string AdviceKey = "reasoning.advice";

    private readonly IMessageBus _bus;

    public ReasoningAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ReasoningAgent> logger, ISubstrateProvider substrate)
        : base(bus, activity, logger, substrate) => _bus = bus;

    public override string Name => "Reasoning";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    protected override string SubstrateClass => "fast-medium";
    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        return $"In one concise sentence, offer relevant reasoning or a useful angle on: {text}";
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => "(reasoning unavailable)";

    protected override void Publish(Envelope envelope, string result, SubstrateResult? diagnostics)
    {
        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(AdviceKey, result));
        _bus.Publish(Topics.Advisories, advisory);
    }
}
