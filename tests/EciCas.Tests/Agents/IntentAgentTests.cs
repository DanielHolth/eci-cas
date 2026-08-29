using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class IntentAgentTests
{
    [Fact]
    public async Task PublishesProposal_IncorporatingBundleAdvice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var proposals = bus.Subscribe(Topics.Proposal);
        var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider());

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
            .With(PerceptionAgent.TextKey, "how's the weather")
            .With(ReasoningAgent.AdviceKey, "check a forecast")
            .With(SelfAgent.AdviceKey, "I'm ECI, here to help."));

        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.True(proposals.TryRead(out var proposal));
        var reply = proposal!.Meta.Get<string>(IntentAgent.ReplyKey);
        Assert.NotNull(reply);
        Assert.Contains("check a forecast", reply);
    }
}
