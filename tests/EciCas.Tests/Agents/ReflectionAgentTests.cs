using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ReflectionAgentTests
{
    [Fact]
    public async Task PublishesIdea_WhenBelowGenerationCap()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var control = bus.Subscribe(Topics.SystemControl);
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new MockSubstrateProvider(),
            Options.Create(new AgentSubstrateManifest { Agents = { ["Reflection"] = "slow-low" } }),
            Options.Create(new ReflectionOptions { MaxIdeaGeneration = 1 }));

        var conclusion = Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "tacos sound good"), generation: 0);
        await agent.HandleAsync(conclusion, CancellationToken.None);

        Assert.True(control.TryRead(out _));
        Assert.True(perceptions.TryRead(out var idea));
        Assert.Equal("self", idea!.Meta.Get<string>(ReflectionAgent.TriggeredByKey));
        Assert.Equal(1, idea.Generation);
        Assert.NotEqual(conclusion.CorrelationId, idea.CorrelationId);
    }

    [Fact]
    public async Task DoesNotPublishIdea_AtGenerationCap()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new MockSubstrateProvider(),
            Options.Create(new AgentSubstrateManifest { Agents = { ["Reflection"] = "slow-low" } }),
            Options.Create(new ReflectionOptions { MaxIdeaGeneration = 1 }));

        var conclusion = Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "tacos sound good"), generation: 1);
        await agent.HandleAsync(conclusion, CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));
    }
}
