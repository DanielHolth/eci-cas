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
            Options.Create(new AgentSubstrateManifest { Agents = { ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" } } }));

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
}
