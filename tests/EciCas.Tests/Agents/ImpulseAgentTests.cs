using System.Threading.Channels;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class ImpulseAgentTests
{
    private static (ImpulseAgent Agent, ChannelReader<Envelope> Advisories, ChannelReader<Envelope> Proposals) Create()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var proposals = bus.Subscribe(Topics.Proposal);
        var agent = new ImpulseAgent(bus, activity, NullLogger<ImpulseAgent>.Instance);
        return (agent, advisories, proposals);
    }

    [Fact]
    public async Task WhenTextIsCritical_PublishesAdvisoryAndReflexProposal()
    {
        var (agent, advisories, proposals) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "emergency, need help now"));

        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal(Severity.Elevated, advisory!.Severity);

        Assert.True(proposals.TryRead(out var proposal));
        Assert.Equal("Impulse", proposal!.PublishedBy);
        Assert.False(string.IsNullOrEmpty(proposal.Meta.Get<string>(IntentAgent.ReplyKey)));
    }

    [Fact]
    public async Task WhenTextIsRoutine_PublishesAdvisoryOnly()
    {
        var (agent, advisories, proposals) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "what's the weather"));

        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out _));
        Assert.False(proposals.TryRead(out _));
    }
}
