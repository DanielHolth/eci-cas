using System.Text;
using System.Text.Json.Nodes;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Identity;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Intent;

/// <summary>
/// Composes the considered reply: a substrate call over the perceived text
/// plus whatever Impulse/Librarian/Identity contributed to the bundle. Cognitive
/// tier — the mock-echo placeholder it started as is gone.
/// </summary>
public sealed class IntentAgent : CognitiveAgent<string>
{
    public const string ReplyKey = "intent.reply";

    /// <summary>
    /// What this turn gave Intent to work with: the person's text, the
    /// advisories, the recalled facts and the woken notes. Never the
    /// standing rules, which are identical every turn and are not something
    /// Intent "had to work with" in any sense Reflection needs.
    /// </summary>
    public const string ContextKey = "intent.context";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly RuntimeKnobs _knobs;

    public IntentAgent(IMessageBus bus, BusActivityTracker activity, ILogger<IntentAgent> logger, ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IInstructionStore instructions, RuntimeKnobs knobs)
        : base(bus, activity, logger, substrate, agentSubstrates)
    {
        _bus = bus;
        _instructions = instructions;
        _knobs = knobs;
    }

    public override string Name => "Intent";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope) =>
        _instructions.For(Name) + Environment.NewLine + Environment.NewLine + BuildContext(envelope, _knobs.MaxSentences, _knobs.Tone);

    /// <summary>
    /// Everything this turn actually contributed: the person's text plus
    /// whatever the bundle carried, with the standing rules left out. This
    /// is what Reflection needs. The rules are identical on every turn and
    /// say nothing about what Intent was working with, while costing it the
    /// whole of its own <see cref="PromptCap"/> budget to render.
    /// Recomputed rather than stashed, so it stays a pure function of the
    /// envelope and no per-turn state has to live on the agent.
    /// </summary>
    private static string BuildContext(Envelope envelope, int maxSentences, Tone tone)
    {
        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));

        var prompt = new StringBuilder("Reply to: ").Append(text);

        AppendAdvice(prompt, "Impulse", envelope.Meta.Get<string>(ImpulseAgent.AdviceKey));
        AppendAdvice(prompt, "Identity", envelope.Meta.Get<string>(IdentityAgent.AdviceKey));
        AppendRecalledFacts(prompt, envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey));
        AppendNotes(prompt, envelope.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NotesKey));
        AppendLengthLimit(prompt, maxSentences);
        AppendTone(prompt, tone);

        var revisionConcern = envelope.Meta.Get<string>(GovernanceAgent.RevisionConcernKey);
        if (!string.IsNullOrEmpty(revisionConcern))
        {
            prompt.Append(" [Revise — Security flagged: ").Append(revisionConcern).Append(']');
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The Debug panel's sentence-count slider, rendered the same
    /// bracket-tag way every other advisory is — a per-turn override of
    /// intent.txt's own "one sentence, two at most" default, not a
    /// replacement for it.
    /// </summary>
    private static void AppendLengthLimit(StringBuilder prompt, int maxSentences)
    {
        prompt.Append(" [Length: ")
            .Append(maxSentences == 1 ? "1 sentence" : $"1-{maxSentences} sentences")
            .Append(". Keep it terse.]");
    }

    /// <summary>
    /// The Debug panel's tone slider — a per-turn override sitting alongside
    /// Identity's own advisory rather than replacing it, so a wildly
    /// different mood is still spoken in the persona's own voice.
    /// </summary>
    private static void AppendTone(StringBuilder prompt, Tone tone)
    {
        if (tone == Tone.Neutral)
        {
            return;
        }

        prompt.Append(" [Tone: ").Append(tone).Append('.').Append(']');
    }

    /// <summary>
    /// Thoughts the persona had after past turns, woken by Hindsight because
    /// this one brushed against them. Framed as "Noted before" rather than as
    /// advice, because a note is the persona's own opinion, not an
    /// instruction — Intent weighs it the way it weighs a recalled fact, and
    /// is free to disagree with something it once thought.
    ///
    /// Each note arrives with its age on the front, which is most of what
    /// makes it usable: "3 months ago" and "earlier today" ask to be weighed
    /// differently, and the second one is closer to an echo than a memory.
    /// </summary>
    private static void AppendNotes(StringBuilder prompt, IReadOnlyList<string>? notes)
    {
        if (notes is null || notes.Count == 0)
        {
            return;
        }

        prompt.Append(" [Noted before: ").Append(string.Join("; ", notes.Select(PromptCap.Apply))).Append(']');
    }

    private static void AppendAdvice(StringBuilder prompt, string source, string? advice)

    {
        if (!string.IsNullOrEmpty(advice))
        {
            prompt.Append(" [").Append(source).Append(": ").Append(PromptCap.Apply(advice)).Append(']');
        }
    }

    /// <summary>
    /// Recall's recalled facts as one JSON array, already sorted by
    /// Importance — no English restating of the path convention here, since
    /// ResponseContract already states it once, statically. RecallAgent's
    /// own picking prompt withholds Category/Topic/Subtopic as redundant,
    /// but that's only true there because each of its calls is scoped to
    /// one triple; here picks from every selected triple land in one flat
    /// list, so the full path is the only thing left distinguishing two
    /// facts that share a Subject/Key (e.g. person/family/son vs
    /// person/work/colleague).
    /// </summary>
    private static void AppendRecalledFacts(StringBuilder prompt, IReadOnlyList<ArchiveRecord>? facts)
    {
        // Null and empty are different answers and must not read the same.
        // Null is no advisory at all — Recall never ran, or failed, and Intent
        // has no standing to say anything about the archive. Empty is Recall
        // reporting back: it looked, and there is nothing on file.
        //
        // Both used to produce a byte-identical prompt, and the model filled
        // the silence — the mechanism behind "I know your name, but you
        // haven't told me yours yet." Six tokens turn that invention into an
        // honest "I don't think you've told me." Same argument the degraded
        // notice already won, applied to the faculty that worked.
        if (facts is null)
        {
            return;
        }

        if (facts.Count == 0)
        {
            prompt.Append(" [Recall: nothing on file]");
            return;
        }

        var array = new JsonArray();
        foreach (var f in facts)
        {
            array.Add(new JsonObject
            {
                [$"{f.Category}/{f.Topic}/{f.Subtopic}/{f.Subject}/{f.Key}"] = f.Value,
                ["Importance"] = f.Importance,
            });
        }

        prompt.Append(" [Recall: ").Append(array.ToJsonString()).Append(']');
    }

    protected override string ParseResult(SubstrateResult result) => result.Text.Trim();

    protected override string FallbackResult(Envelope envelope) => _instructions.For(Name, "fallback");

    protected override void Publish(Envelope envelope, string prompt, string result, SubstrateResult? diagnostics, string? degraded)
    {
        // Marked, not just spoken: Intent's fallback sentence sounds honest,
        // but Governance is the one that decides what the person is told, and
        // it can only do that if the fallback says it is one.
        var meta = MetaBag.Empty.With(ReplyKey, result).With(ContextKey, BuildContext(envelope, _knobs.MaxSentences, _knobs.Tone));

        // Which notes Hindsight woke for this turn, carried forward the same
        // way ContextKey is: Derive starts a fresh bag rather than inheriting
        // the bundle's, so without this the lineage dies here and Reflection
        // cannot record what its new note descends from. Diagnostic payload
        // only — Intent has already read the notes themselves as prose.
        if (envelope.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NoteIdsKey) is { Count: > 0 } noteIds)
        {
            meta = meta
                .With(HindsightAgent.NoteIdsKey, noteIds)
                .With(HindsightAgent.EchoDepthKey, envelope.Meta.Get<int>(HindsightAgent.EchoDepthKey));
        }

        var proposal = envelope.Derive(Topics.Proposal, Name, envelope.Severity,
            SubstrateHealth.Mark(meta, degraded));
        _bus.Publish(Topics.Proposal, proposal);
    }
}
