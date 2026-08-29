using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class SelfAgentTests
{
    [Fact]
    public async Task PublishesIdentityAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = new SelfAgent(bus, activity, NullLogger<SelfAgent>.Instance);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.False(string.IsNullOrEmpty(advisory!.Meta.Get<string>(SelfAgent.AdviceKey)));
    }
}
