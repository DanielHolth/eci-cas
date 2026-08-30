using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class IntentAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    [Fact]
    public async Task WhenReplyParrotsReasoningAdvice_FallsBackInstead()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var proposals = bus.Subscribe(Topics.Proposal);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("  Check a forecast!  ", TimeSpan.Zero, 5, 0m)));
        var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, substrate,
            Options.Create(new AgentSubstrateManifest { Agents = { ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" } } }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral, MetaBag.Empty
            .With(PerceptionAgent.TextKey, "how's the weather")
            .With(ReasoningAgent.AdviceKey, "check a forecast"));

        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.True(proposals.TryRead(out var proposal));
        var reply = proposal!.Meta.Get<string>(IntentAgent.ReplyKey);
        Assert.Equal("I'm having trouble thinking that through right now.", reply);
    }

    [Fact]
    public async Task PublishesProposal_IncorporatingBundleAdvice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var proposals = bus.Subscribe(Topics.Proposal);
        var agent = new IntentAgent(bus, activity, NullLogger<IntentAgent>.Instance, new MockSubstrateProvider(),
            Options.Create(new AgentSubstrateManifest { Agents = { ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" } } }));

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
