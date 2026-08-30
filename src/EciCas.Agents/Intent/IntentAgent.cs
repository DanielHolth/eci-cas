using System.Text;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Recall;
using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Intent;

/// <summary>
/// Composes the considered reply: a substrate call over the perceived text
/// plus whatever Impulse/Reasoning/Self contributed to the bundle. Cognitive
/// tier — the mock-echo placeholder from M1 is gone.
/// </summary>
public sealed class IntentAgent : CognitiveAgent<string>
{
    public const string ReplyKey = "intent.reply";

    /// <summary>
    /// Ported verbatim from the Python prototype's
    /// agents/intent/live.py DEFAULT_SYSTEM_INSTRUCTION.
    /// </summary>
    private const string SystemInstruction =
        "You are INTENT: the voice of a multi-agent system. " +
        "Be concise and natural — short, direct replies. " +
        "If Security is red, revise or your message is blocked. If Security is " +
        "yellow, it's a judgment call, not a violation — revise if you can " +
        "address the concern, but it will go out either way.";

    /// <summary>
    /// Ported verbatim from the Python prototype's
    /// agents/intent/contract.py RESPONSE_CONTRACT.
    /// </summary>
    private const string ResponseContract = """
        Reply with your response to the human. Nothing else — no JSON,
        labels, no preamble, no wrapping.

        RULES:
        - One sentence, two at most. Never longer.
        - Answer the question directly. Do not restate it, do not narrate
          your reasoning, do not ask what the human meant.
        - Never start with "You asked", "You mentioned", "I think you're asking",
          or any paraphrase of what the human said.
        - Talk like a person, not a system explaining itself.
        - A Knowledge fact whose path starts with "system/" describes YOU (your
          own name, traits, preferences) — never attribute it to the human. A
          fact whose path starts with "person/" describes the human or someone
          they've told you about.
        """;

    private readonly IMessageBus _bus;

    public IntentAgent(IMessageBus bus, BusActivityTracker activity, ILogger<IntentAgent> logger, ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates)
        : base(bus, activity, logger, substrate, agentSubstrates) => _bus = bus;

    public override string Name => "Intent";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope)
    {
        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));

        var prompt = new StringBuilder(SystemInstruction).Append('\n').Append(ResponseContract)
            .Append("\n\nReply to: ").Append(text);

        AppendAdvice(prompt, "Impulse", envelope.Meta.Get<string>(ImpulseAgent.AdviceKey));
        AppendAdvice(prompt, "Reasoning", envelope.Meta.Get<string>(ReasoningAgent.AdviceKey));
        AppendAdvice(prompt, "Recall", envelope.Meta.Get<string>(RecallAgent.ResultsKey));
        AppendAdvice(prompt, "Self", envelope.Meta.Get<string>(SelfAgent.AdviceKey));

        var revisionConcern = envelope.Meta.Get<string>(GovernanceAgent.RevisionConcernKey);
        if (!string.IsNullOrEmpty(revisionConcern))
        {
            prompt.Append(" [Revise — Security flagged: ").Append(revisionConcern).Append(']');
        }

        return prompt.ToString();
    }

    private static void AppendAdvice(StringBuilder prompt, string source, string? advice)
    {
        if (!string.IsNullOrEmpty(advice))
        {
            prompt.Append(" [").Append(source).Append(": ").Append(PromptCap.Apply(advice)).Append(']');
        }
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => "I'm having trouble thinking that through right now.";

    protected override void Publish(Envelope envelope, string result, SubstrateResult? diagnostics)
    {
        // Only a real substrate reply can parrot — FallbackResult is fixed
        // text with nothing upstream to echo, so this can't loop back into
        // itself when CognitiveAgent<T>'s catch re-invokes Publish below.
        if (diagnostics is not null && ParrotGuard.IsParroting(result, envelope.Meta.Get<string>(ReasoningAgent.AdviceKey)))
        {
            throw new InvalidOperationException($"{Name} response parrots Reasoning's advisory instead of voicing it: {result[..Math.Min(result.Length, 200)]}");
        }

        var proposal = envelope.Derive(Topics.Proposal, Name, envelope.Severity, MetaBag.Empty.With(ReplyKey, result));
        _bus.Publish(Topics.Proposal, proposal);
    }
}
