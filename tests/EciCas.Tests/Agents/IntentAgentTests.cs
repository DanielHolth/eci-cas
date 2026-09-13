using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.TurnWindow;
using EciCas.Agents.Utterances;
using EciCas.Agents.Identity;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class IntentAgentTests
{
    [Fact]
    public async Task PublishesProposal_IncorporatingBundleAdviceAndRecalledFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var proposals = bus.Subscribe(Topics.Proposal);
        var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider(),
            Options.Create(new SubstrateOptions { Agents = { ["Intent"] = new SubstrateAgentEntry() } }), ShippedInstructions.Store, new RuntimeKnobs(),
            new TurnWindowAgent(bus, activity, NullLogger<TurnWindowAgent>.Instance));

        var facts = new[] { new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow) };
        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
            .With(PerceptionAgent.TextKey, "how's the weather")
            .With(IdentityAgent.AdviceKey, "I'm ECI, here to help.")
            .With(ConsultAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)facts));

        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.True(proposals.TryRead(out var proposal));
        var reply = proposal!.Meta.Get<string>(IntentAgent.ReplyKey);
        Assert.NotNull(reply);
    }

    /// <summary>
    /// The bug this pins: the published context used to be the whole prompt,
    /// standing rules first, and Reflection renders it through a 240-char
    /// PromptCap. The rules alone ran to 840 characters, so Reflection saw
    /// boilerplate and never a turn. What Intent was given has to survive the
    /// cap, which means the rules must not be in front of it.
    /// </summary>
    [Fact]
    public async Task PublishedContext_CarriesTheTurnAndNotTheStandingRules()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var proposals = bus.Subscribe(Topics.Proposal);
        var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider(),
            Options.Create(new SubstrateOptions { Agents = { ["Intent"] = new SubstrateAgentEntry() } }), ShippedInstructions.Store, new RuntimeKnobs(),
            new TurnWindowAgent(bus, activity, NullLogger<TurnWindowAgent>.Instance));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
            .With(PerceptionAgent.TextKey, "when is the wedding")
            .With(IdentityAgent.AdviceKey, "warm, brief"));

        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.True(proposals.TryRead(out var proposal));
        var context = proposal!.Meta.Get<string>(IntentAgent.ContextKey);
        Assert.NotNull(context);

        Assert.Contains("when is the wedding", context);
        Assert.Contains("warm, brief", context);
        Assert.DoesNotContain("RULES:", context);
        Assert.DoesNotContain("spokesperson", context);

        // Reflection's window is 240 characters and it renders from the
        // front, so the turn has to be inside it, not merely present.
        Assert.Contains("when is the wedding", PromptCap.Apply(context));
    }

    /// <summary>
    /// An empty recall and an absent one used to build a byte-identical
    /// prompt, and the model bridged the gap by inventing — the mechanism
    /// behind "I know your name, but you haven't told me yours yet." Recall
    /// always publishes the key, even with nothing in it, so the two cases
    /// are distinguishable here and must read differently.
    /// </summary>
    [Fact]
    public async Task RecallThatLookedAndFoundNothing_SaysSo_WhereRecallThatNeverRanIsSilent()
    {
        static async Task<string> ContextFor(MetaBag meta)
        {
            var activity = new BusActivityTracker();
            var bus = new ChannelBus(activity);
            var proposals = bus.Subscribe(Topics.Proposal);
            var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider(),
                Options.Create(new SubstrateOptions { Agents = { ["Intent"] = new SubstrateAgentEntry() } }), ShippedInstructions.Store, new RuntimeKnobs(),
            new TurnWindowAgent(bus, activity, NullLogger<TurnWindowAgent>.Instance));

            await agent.HandleAsync(Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, meta), CancellationToken.None);
            Assert.True(proposals.TryRead(out var proposal));
            return proposal!.Meta.Get<string>(IntentAgent.ContextKey)!;
        }

        var turn = MetaBag.Empty.With(PerceptionAgent.TextKey, "what's my name");

        var neverRan = await ContextFor(turn);
        var foundNothing = await ContextFor(turn.With(ConsultAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)[]));

        Assert.DoesNotContain("Recall", neverRan);
        Assert.Contains("[Recall: nothing on file]", foundNothing);
        Assert.NotEqual(neverRan, foundNothing);
    }

    /// <summary>
    /// A recalled sentence arrives with what it is about in front of it.
    ///
    /// The bug this pins: seven rows that all mentioned kids reached Intent
    /// as seven bare sentences, and nothing in the slate said which one was
    /// about the speaker and which about a daughter -- the extractor had
    /// written that on every row since facts arrived classified, and no
    /// reader had ever asked for it. A row that already opens with its own
    /// subject is left alone rather than stuttering it twice.
    /// </summary>
    [Fact]
    public async Task ARecalledFactSaysWhatItIsAbout()
    {
        static async Task<string> ContextFor(params ArchiveRecord[] facts)
        {
            var activity = new BusActivityTracker();
            var bus = new ChannelBus(activity);
            var proposals = bus.Subscribe(Topics.Proposal);
            var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider(),
                Options.Create(new SubstrateOptions { Agents = { ["Intent"] = new SubstrateAgentEntry() } }), ShippedInstructions.Store, new RuntimeKnobs(),
                new TurnWindowAgent(bus, activity, NullLogger<TurnWindowAgent>.Instance));

            await agent.HandleAsync(Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
                .With(PerceptionAgent.TextKey, "tell me about my family")
                .With(ConsultAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)facts)), CancellationToken.None);

            Assert.True(proposals.TryRead(out var proposal));
            return proposal!.Meta.Get<string>(IntentAgent.ContextKey)!;
        }

        static ArchiveRecord Recalled(string entity, string sentence) => new(
            Category: string.Empty, Topic: string.Empty, Subtopic: string.Empty,
            Subject: entity, Key: string.Empty, Value: string.Empty,
            Timestamp: DateTimeOffset.UtcNow, Sentence: sentence);

        var context = await ContextFor(
            Recalled("Maria Benita", "her birthday is 2011-01-10"),
            Recalled("Marcus", "Marcus is my oldest son"),
            Recalled("", "I have 3 kids"));

        Assert.Contains("Maria Benita: her birthday is 2011-01-10", context);
        Assert.Contains("Marcus is my oldest son", context);
        Assert.DoesNotContain("Marcus: Marcus", context);
        Assert.Contains("I have 3 kids", context);
    }
}
