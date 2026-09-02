using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
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
            Options.Create(new AgentSubstrateManifest { Agents = { ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" } } }), ShippedInstructions.Store);

        var facts = new[] { new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow) };
        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
            .With(PerceptionAgent.TextKey, "how's the weather")
            .With(IdentityAgent.AdviceKey, "I'm ECI, here to help.")
            .With(RecallAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)facts));

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
            Options.Create(new AgentSubstrateManifest { Agents = { ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" } } }), ShippedInstructions.Store);

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
}
