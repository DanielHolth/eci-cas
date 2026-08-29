using EciCas.Agents.Governance;
using EciCas.Agents.Intent;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class GovernanceAgentTests
{
    private static GovernanceAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, string[] roster) =>
        new(bus, activity, NullLogger<GovernanceAgent>.Instance, Options.Create(new GovernanceOptions { BundleRoster = roster }));

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public async Task WhenAdvisoriesArriveInAnyOrder_ProducesSameBundle(string first)
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var bundleReader = bus.Subscribe(Topics.Bundle);
        var agent = CreateAgent(bus, activity, ["A", "B"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var advisoryA = perception.Derive(Topics.Advisories, "A", Severity.Neutral);
        var advisoryB = perception.Derive(Topics.Advisories, "B", Severity.Elevated);
        var advisories = first == "A" ? (advisoryA, advisoryB) : (advisoryB, advisoryA);

        await agent.HandleAsync(advisories.Item1, CancellationToken.None);
        Assert.False(bundleReader.TryRead(out _));

        await agent.HandleAsync(advisories.Item2, CancellationToken.None);
        Assert.True(bundleReader.TryRead(out var bundle));
        Assert.Equal(Severity.Elevated, bundle!.Severity);
        Assert.Equal(perception.CorrelationId, bundle.CorrelationId);
    }

    [Fact]
    public async Task WhenAdvisorMissing_NeverBundles()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var bundleReader = bus.Subscribe(Topics.Bundle);
        var agent = CreateAgent(bus, activity, ["A", "B"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        await agent.HandleAsync(perception.Derive(Topics.Advisories, "A", Severity.Neutral), CancellationToken.None);

        Assert.False(bundleReader.TryRead(out _));
    }

    [Fact]
    public async Task Action_NeverExecutes_BeforeSecurityClears()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var redVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Red).With(IntentAgent.ReplyKey, "should never run"));
        await agent.HandleAsync(redVerdict, CancellationToken.None);

        Assert.False(actionReader.TryRead(out _));
    }

    [Fact]
    public async Task Action_Executes_AfterGreenVerdict()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var greenVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "ok"));
        await agent.HandleAsync(greenVerdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out _));
    }
}
