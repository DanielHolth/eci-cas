using System.Text;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Perception;
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

    /// <summary>The exact prompt this call sent the substrate — Reflection's window into what Intent actually had to work with, not just what it said.</summary>
    public const string PromptKey = "intent.prompt";

    /// <summary>
    /// Adapted from the Python prototype's agents/intent/live.py
    /// DEFAULT_SYSTEM_INSTRUCTION — the internal agent name ("INTENT") is
    /// dropped since it's meaningless to the model without the rest of the
    /// swarm's context and was leaking into replies as if it were an
    /// identity to report (e.g. "Can you list all names you have?" -> "I'm
    /// called INTENT.").
    /// </summary>
    private const string SystemInstruction =
        "You are the spokesperson on behalf of a collective of emerging agents. " +
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
        - A recalled fact's path is category/topic/subtopic/subject/key. If the
          path starts with "system/" (e.g. system/identity/persona/this/name),
          it describes YOU, the assistant — your own name, traits, or
          preferences — never attribute it to the human or anyone else. Any
          other category describes the human or someone they've told you
          about.
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
        AppendAdvice(prompt, "Self", envelope.Meta.Get<string>(SelfAgent.AdviceKey));
        AppendRecalledFacts(prompt, envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey));

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

    /// <summary>
    /// Recall's recalled facts as one section, already sorted by Importance —
    /// no External/Internal split. RecallAgent's own picking prompt withholds
    /// Category/Topic/Subtopic as redundant, but that's only true there
    /// because each of its calls is scoped to one triple; here picks from
    /// every selected triple land in one flat list, so the full path is the
    /// only thing left distinguishing two facts that share a Subject/Key
    /// (e.g. person/family/son vs person/work/colleague). The self/human
    /// distinction is also baked into each fact's own phrasing rather than
    /// left to the abstract category==="system" rule in ResponseContract
    /// above: a small substrate call reliably drops a rule stated once and
    /// applied many tokens later, especially against a subject as generic as
    /// "this" (see the seeded system/identity/persona record in Program.cs).
    /// </summary>
    private static void AppendRecalledFacts(StringBuilder prompt, IReadOnlyList<ArchiveRecord>? facts)
    {
        if (facts is null || facts.Count == 0)
        {
            return;
        }

        var joined = string.Join("; ", facts.Select(FormatFact));
        prompt.Append(" [Recall: ").Append(PromptCap.Apply(joined)).Append(']');
    }

    private static string FormatFact(ArchiveRecord f)
    {
        var path = $"{f.Category}/{f.Topic}/{f.Subtopic}";
        return string.Equals(f.Category, "system", StringComparison.OrdinalIgnoreCase)
            ? $"(about you, the assistant; {path}) your {f.Key} is {f.Value}"
            : $"(about the human or someone they mentioned; {path}) {f.Subject}'s {f.Key} is {f.Value}";
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => "I'm having trouble thinking that through right now.";

    protected override void Publish(Envelope envelope, string prompt, string result, SubstrateResult? diagnostics)
    {
        var proposal = envelope.Derive(Topics.Proposal, Name, envelope.Severity,
            MetaBag.Empty.With(ReplyKey, result).With(PromptKey, prompt));
        _bus.Publish(Topics.Proposal, proposal);
    }
}
