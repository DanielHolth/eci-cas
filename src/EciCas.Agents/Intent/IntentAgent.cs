using System.Text;
using EciCas.Agents.Impulse;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Intent;

/// <summary>
/// Composes the considered reply: a substrate call over the perceived text
/// plus whatever Impulse/Reasoning/Self contributed to the bundle. Cognitive
/// tier — the mock-echo placeholder from M1 is gone.
/// </summary>
public sealed class IntentAgent : CognitiveAgent<string>
{
    public const string ReplyKey = "intent.reply";

    private readonly IMessageBus _bus;

    public IntentAgent(IMessageBus bus, BusActivityTracker activity, ILogger<IntentAgent> logger, ISubstrateProvider substrate)
        : base(bus, activity, logger, substrate) => _bus = bus;

    public override string Name => "Intent";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    protected override string SubstrateClass => "fast-medium";
    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        var prompt = new StringBuilder("Reply to: ").Append(text);

        AppendAdvice(prompt, "Impulse", envelope.Meta.Get<string>(ImpulseAgent.AdviceKey));
        AppendAdvice(prompt, "Reasoning", envelope.Meta.Get<string>(ReasoningAgent.AdviceKey));
        AppendAdvice(prompt, "Self", envelope.Meta.Get<string>(SelfAgent.AdviceKey));

        return prompt.ToString();
    }

    private static void AppendAdvice(StringBuilder prompt, string source, string? advice)
    {
        if (!string.IsNullOrEmpty(advice))
        {
            prompt.Append(" [").Append(source).Append(": ").Append(advice).Append(']');
        }
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => "I'm having trouble thinking that through right now.";

    protected override void Publish(Envelope envelope, string result, SubstrateResult? diagnostics)
    {
        var proposal = envelope.Derive(Topics.Proposal, Name, envelope.Severity, MetaBag.Empty.With(ReplyKey, result));
        _bus.Publish(Topics.Proposal, proposal);
    }
}
