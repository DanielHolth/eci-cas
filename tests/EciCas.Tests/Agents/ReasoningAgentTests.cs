using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class ReasoningAgentTests
{
    [Fact]
    public async Task PublishesAdvisory_WithSubstrateText()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, new MockSubstrateProvider());

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "what's for dinner?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal(perception.CorrelationId, advisory!.CorrelationId);
        Assert.False(string.IsNullOrEmpty(advisory.Meta.Get<string>(ReasoningAgent.AdviceKey)));
    }
}
