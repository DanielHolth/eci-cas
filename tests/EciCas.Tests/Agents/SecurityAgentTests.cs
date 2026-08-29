using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class SecurityAgentTests
{
    private const string Rules = """
    { "rules": [ { "id": "flag-secret", "verdict": "Yellow", "concern": "looks like a secret", "any": ["\\bsecret\\b"] } ] }
    """;

    private static SecurityAgent CreateAgent(IMessageBus bus, BusActivityTracker activity) =>
        new(bus, activity, NullLogger<SecurityAgent>.Instance, SecurityRuleSet.Parse(Rules));

    [Fact]
    public async Task CleanReply_ProducesGreenVerdict_WithNoConcern()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var verdicts = bus.Subscribe(Topics.Verdict);
        var agent = CreateAgent(bus, activity);

        var proposal = Envelope.Create(Topics.Proposal, "Intent", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "the weather is sunny"));
        await agent.HandleAsync(proposal, CancellationToken.None);

        Assert.True(verdicts.TryRead(out var verdict));
        Assert.Equal(Verdict.Green, verdict!.Meta.Get<Verdict>(SecurityAgent.VerdictKey));
        Assert.Null(verdict.Meta.Get<string>(SecurityAgent.ConcernKey));
    }

    [Fact]
    public async Task FlaggedReply_ProducesVerdictAndConcern()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var verdicts = bus.Subscribe(Topics.Verdict);
        var agent = CreateAgent(bus, activity);

        var proposal = Envelope.Create(Topics.Proposal, "Intent", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "here is my secret plan"));
        await agent.HandleAsync(proposal, CancellationToken.None);

        Assert.True(verdicts.TryRead(out var verdict));
        Assert.Equal(Verdict.Yellow, verdict!.Meta.Get<Verdict>(SecurityAgent.VerdictKey));
        Assert.Equal("looks like a secret", verdict.Meta.Get<string>(SecurityAgent.ConcernKey));
    }

    [Fact]
    public async Task ReflexMarker_SurvivesTheHopToTheVerdict()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var verdicts = bus.Subscribe(Topics.Verdict);
        var agent = CreateAgent(bus, activity);

        var proposal = Envelope.Create(Topics.Proposal, "Impulse", Severity.Elevated,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "on it").With(ImpulseAgent.ReflexKey, true));
        await agent.HandleAsync(proposal, CancellationToken.None);

        Assert.True(verdicts.TryRead(out var verdict));
        Assert.True(verdict!.Meta.Get<bool>(ImpulseAgent.ReflexKey));
    }
}
