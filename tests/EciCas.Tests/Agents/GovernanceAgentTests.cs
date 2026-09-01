using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Recall;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class GovernanceAgentTests
{
    private static GovernanceAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, string[] roster) =>
        new(bus, activity, NullLogger<GovernanceAgent>.Instance, Options.Create(new GovernanceOptions { BundleRoster = roster }),
            new JsonlAgentStateStore(Path.GetTempFileName()));

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
    public async Task RedVerdict_NeverSpeaksTheOriginalReply_OnlyABlockedNotice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var redVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Red).With(IntentAgent.ReplyKey, "should never run verbatim"));
        await agent.HandleAsync(redVerdict, CancellationToken.None);

        // Red still reaches Action (per the gating matrix: a deterministic
        // Blocked notice, immediately, no revision attempt) but the
        // original reply text must never be the one spoken.
        Assert.True(actionReader.TryRead(out var action));
        var spoken = action!.Meta.Get<string>(IntentAgent.ReplyKey);
        Assert.DoesNotContain("should never run verbatim", spoken);
    }

    [Fact]
    public async Task YellowVerdict_TriggersOneRevisionPass_ThenProceedsRegardless()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var bundleReader = bus.Subscribe(Topics.Bundle);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        // Empty roster completes the bundle immediately on Perception — drain
        // that first (unrelated) bundle before looking for the revision one.
        Assert.True(bundleReader.TryRead(out _));

        var firstYellow = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Yellow).With(SecurityAgent.ConcernKey, "ambiguous").With(IntentAgent.ReplyKey, "first draft"));
        await agent.HandleAsync(firstYellow, CancellationToken.None);

        // One revision pass: re-issued on Bundle, not yet an Action.
        Assert.True(bundleReader.TryRead(out var revisionBundle));
        Assert.Equal("ambiguous", revisionBundle!.Meta.Get<string>(GovernanceAgent.RevisionConcernKey));
        Assert.False(actionReader.TryRead(out _));

        var secondYellow = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Yellow).With(SecurityAgent.ConcernKey, "still ambiguous").With(IntentAgent.ReplyKey, "revised draft"));
        await agent.HandleAsync(secondYellow, CancellationToken.None);

        // Revision passes exhausted: proceeds to Action anyway.
        Assert.True(actionReader.TryRead(out var action));
        Assert.Equal("revised draft", action!.Meta.Get<string>(IntentAgent.ReplyKey));
        Assert.False(bundleReader.TryRead(out _));
    }

    [Fact]
    public async Task ReflexVerdict_ActsButDoesNotConclude()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var conclusionReader = bus.Subscribe(Topics.Conclusion);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Elevated);
        await agent.HandleAsync(perception, CancellationToken.None);

        var reflexVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Elevated,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "on it")
                .With(ImpulseAgent.ReflexKey, true));
        await agent.HandleAsync(reflexVerdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out _));
        Assert.False(conclusionReader.TryRead(out _));

        var consideredVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Elevated,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "here's the full answer"));
        await agent.HandleAsync(consideredVerdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out _));
        Assert.True(conclusionReader.TryRead(out _));
    }

    [Fact]
    public async Task RedVerdict_AttachesExpressionAndNudgesImpulse()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var systemControlReader = bus.Subscribe(Topics.SystemControl);
        var store = new JsonlAgentStateStore(Path.GetTempFileName());
        var agent = new GovernanceAgent(bus, activity, NullLogger<GovernanceAgent>.Instance,
            Options.Create(new GovernanceOptions { BundleRoster = [] }), store);
        var impulse = new ImpulseAgent(bus, activity, NullLogger<ImpulseAgent>.Instance, store);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var redVerdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Red).With(SecurityAgent.ConcernKey, "policy violation")
                .With(IntentAgent.ReplyKey, "should never run verbatim"));
        await agent.HandleAsync(redVerdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        Assert.True(action!.Meta.Get<bool>(GovernanceAgent.SecurityAlertKey));
        Assert.False(string.IsNullOrEmpty(action.Meta.Get<string>(GovernanceAgent.ExpressionKey)));

        Assert.True(systemControlReader.TryRead(out var control));
        await impulse.HandleAsync(control!, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Single(records);
        var vectors = System.Text.Json.JsonSerializer.Deserialize<DriveVectors>(records[0].Content)!;
        Assert.True(vectors.Urgency > new DriveVectors().Urgency);
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

    /// <summary>
    /// The notice has to be native text, not a generated apology: the whole
    /// point is that it fires when the substrate that would generate one is
    /// the thing that's down.
    /// </summary>
    [Fact]
    public async Task WhenIntentIsDegraded_TheNoticeReplacesTheReplyEntirely()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, []);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        var verdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "canned fallback")
                .With(SubstrateHealth.DegradedKey, SubstrateHealth.TimedOut));
        await agent.HandleAsync(verdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        var spoken = action!.Meta.Get<string>(IntentAgent.ReplyKey)!;
        Assert.DoesNotContain("canned fallback", spoken);
        Assert.Contains(SubstrateHealth.TimedOut, spoken);
        Assert.True(action.Meta.Get<bool>(GovernanceAgent.DegradedKey));
    }

    [Fact]
    public async Task WhenAnAdvisorIsDegraded_TheReplyStandsAndTheNoticeIsAppended()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, ["Recall"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        await agent.HandleAsync(perception.Derive(Topics.Advisories, "Recall", Severity.Neutral,
            MetaBag.Empty.With(SubstrateHealth.DegradedKey, SubstrateHealth.Unreachable)), CancellationToken.None);

        var verdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "a real answer"));
        await agent.HandleAsync(verdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        var spoken = action!.Meta.Get<string>(IntentAgent.ReplyKey)!;
        Assert.StartsWith("a real answer", spoken);
        Assert.Contains("Recall", spoken);
    }

    [Fact]
    public async Task WhenEveryAdvisorAnswersCleanly_ThereIsNoNotice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, ["Recall"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        await agent.HandleAsync(perception.Derive(Topics.Advisories, "Recall", Severity.Neutral), CancellationToken.None);

        var verdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "a real answer"));
        await agent.HandleAsync(verdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        Assert.Equal("a real answer", action!.Meta.Get<string>(IntentAgent.ReplyKey));
        Assert.False(action.Meta.Get<bool>(GovernanceAgent.DegradedKey));
    }

    /// <summary>A blocked turn says one thing and nothing else — a hedge about groundedness would only muddy it.</summary>
    [Fact]
    public async Task RedVerdict_CarriesNoDegradedNotice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, ["Recall"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        await agent.HandleAsync(perception.Derive(Topics.Advisories, "Recall", Severity.Neutral,
            MetaBag.Empty.With(SubstrateHealth.DegradedKey, SubstrateHealth.Unreachable)), CancellationToken.None);

        var verdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Red).With(IntentAgent.ReplyKey, "blocked draft"));
        await agent.HandleAsync(verdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        Assert.DoesNotContain("less grounded", action!.Meta.Get<string>(IntentAgent.ReplyKey)!);
    }

    /// <summary>
    /// Impulse appraises the face; Governance is only the courier. It has to
    /// survive the hop, because the verdict envelope never carried it — the
    /// bundle state is the only place it still exists by the time the action
    /// is built.
    /// </summary>
    [Fact]
    public async Task ImpulsesExpression_ReachesTheAction()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var actionReader = bus.Subscribe(Topics.Action);
        var agent = CreateAgent(bus, activity, ["Impulse"]);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);
        await agent.HandleAsync(perception.Derive(Topics.Advisories, "Impulse", Severity.Neutral,
            MetaBag.Empty.With(ImpulseAgent.ExpressionKey, "warm")), CancellationToken.None);

        var verdict = perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
            MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green).With(IntentAgent.ReplyKey, "sure"));
        await agent.HandleAsync(verdict, CancellationToken.None);

        Assert.True(actionReader.TryRead(out var action));
        Assert.Equal("warm", action!.Meta.Get<string>(GovernanceAgent.ExpressionKey));
    }
}
