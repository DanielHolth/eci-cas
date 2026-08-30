using System.Text.Json;
using System.Threading.Channels;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class ImpulseAgentTests
{
    private static (ImpulseAgent Agent, ChannelReader<Envelope> Advisories, ChannelReader<Envelope> Proposals, IArchiveStore Store) Create()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var proposals = bus.Subscribe(Topics.Proposal);
        var store = new JsonlArchiveStore(Path.GetTempFileName());
        var agent = new ImpulseAgent(bus, activity, NullLogger<ImpulseAgent>.Instance, store);
        return (agent, advisories, proposals, store);
    }

    [Fact]
    public async Task WhenTextIsCritical_PublishesAdvisoryAndReflexProposal()
    {
        var (agent, advisories, proposals, _) = Create();
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
        var (agent, advisories, proposals, _) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "what's the weather"));

        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out _));
        Assert.False(proposals.TryRead(out _));
    }

    [Fact]
    public async Task WhenTextIsCritical_NudgesAndPersistsDriveVectors()
    {
        var (agent, _, _, store) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "emergency, need help now"));

        await agent.HandleAsync(perception, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Single(records);
        Assert.Equal(ArchiveDomain.Internal, records[0].Domain);

        var vectors = JsonSerializer.Deserialize<DriveVectors>(records[0].Content)!;
        var baseline = new DriveVectors();
        Assert.True(vectors.Urgency > baseline.Urgency);
        Assert.True(vectors.Fatigue > baseline.Fatigue);
    }

    [Fact]
    public async Task WhenTextIsApproving_NudgesWarmerInstantly()
    {
        var (agent, _, _, store) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "great job on that"));

        await agent.HandleAsync(perception, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Single(records);
        var vectors = JsonSerializer.Deserialize<DriveVectors>(records[0].Content)!;
        var baseline = new DriveVectors();
        Assert.True(vectors.Temperature > baseline.Temperature);
        Assert.True(vectors.SocialDrive > baseline.SocialDrive);
    }

    [Fact]
    public async Task WhenTextIsDisapproving_NudgesCoolerInstantly()
    {
        var (agent, _, _, store) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "that's wrong, try again"));

        await agent.HandleAsync(perception, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Single(records);
        var vectors = JsonSerializer.Deserialize<DriveVectors>(records[0].Content)!;
        var baseline = new DriveVectors();
        Assert.True(vectors.Temperature < baseline.Temperature);
    }

    [Fact]
    public async Task WhenTextIsRoutine_DoesNotWriteDriveVectors()
    {
        var (agent, _, _, store) = Create();
        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "what's the weather"));

        await agent.HandleAsync(perception, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Empty(records);
    }
}
