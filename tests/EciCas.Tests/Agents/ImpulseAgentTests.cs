using System.Text.Json;
using System.Threading.Channels;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Archivist;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class ImpulseAgentTests
{
    private static (ImpulseAgent Agent, ChannelReader<Envelope> Advisories, ChannelReader<Envelope> Proposals, IAgentStateStore Store) Create()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var proposals = bus.Subscribe(Topics.Proposal);
        var store = new JsonlAgentStateStore(Path.GetTempFileName());
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
    public async Task TwoProfiles_KeepSeparateDriveState()
    {
        // The point of per-profile drive state: warmth earned by one person
        // must not colour how the persona meets the next one. Asserted on
        // what each profile's record ends up holding, not on the nudge math.
        var (agent, _, _, store) = Create();

        await agent.HandleAsync(Perceive("thanks, great job", "daniel"), CancellationToken.None);
        await agent.HandleAsync(Perceive("that's wrong, terrible", "ada"), CancellationToken.None);

        var daniel = await ReadDriveAsync(store, ImpulseAgent.DrivePathFor("daniel"));
        var ada = await ReadDriveAsync(store, ImpulseAgent.DrivePathFor("ada"));

        // Compared against the resting baseline, not zero — Temperature is
        // clamped to 0..1 and starts at its default, so "cooler" means below
        // where a profile that had said nothing would still be.
        var baseline = new DriveVectors();
        Assert.True(daniel.Temperature > baseline.Temperature);
        Assert.True(ada.Temperature < baseline.Temperature);

        // And the device-wide state neither of them named stays untouched.
        Assert.Empty(await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None));
    }

    [Fact]
    public async Task InputWithNoProfile_NudgesTheDeviceWideDriveState()
    {
        // The console loop and Reflection's own ideas belong to nobody, and a
        // single-user install never sends a profile at all — both keep using
        // the unsuffixed path they always did.
        var (agent, _, _, store) = Create();

        await agent.HandleAsync(Perceive("thanks, great job", profileId: null), CancellationToken.None);

        Assert.True((await ReadDriveAsync(store, ImpulseAgent.DrivePath)).Temperature > new DriveVectors().Temperature);
    }

    [Fact]
    public async Task FrustrationCarryingAProfile_NudgesOnlyThatProfile()
    {
        var (agent, _, _, store) = Create();
        var control = Envelope.Create(Topics.SystemControl, "Governance", Severity.Elevated,
            MetaBag.Empty
                .With(ArchivistAgent.ControlKindKey, GovernanceAgent.FrustrationKind)
                .With(PerceptionAgent.ProfileKey, "daniel"));

        await agent.HandleAsync(control, CancellationToken.None);

        Assert.True((await ReadDriveAsync(store, ImpulseAgent.DrivePathFor("daniel"))).Urgency > new DriveVectors().Urgency);
        Assert.Empty(await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None));
    }

    private static Envelope Perceive(string text, string? profileId)
    {
        var meta = MetaBag.Empty.With(PerceptionAgent.TextKey, text);
        return Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            profileId is null ? meta : meta.With(PerceptionAgent.ProfileKey, profileId));
    }

    private static async Task<DriveVectors> ReadDriveAsync(IAgentStateStore store, string path)
    {
        var records = await store.LookupAsync([path], maxPerPath: 1, CancellationToken.None);
        return JsonSerializer.Deserialize<DriveVectors>(Assert.Single(records).Content)!;
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

    [Fact]
    public async Task WhenReflectionReportsMood_ColorsDriveVectorsSlowly()
    {
        var (agent, _, _, store) = Create();
        var reflected = Envelope.Create(Topics.SystemControl, "Reflection", Severity.Neutral,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ReflectionAgent.ReflectedKind)
                .With(ReflectionAgent.MoodKey, "curious"));

        await agent.HandleAsync(reflected, CancellationToken.None);

        var records = await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None);
        Assert.Single(records);
        var vectors = JsonSerializer.Deserialize<DriveVectors>(records[0].Content)!;
        var baseline = new DriveVectors();
        Assert.True(vectors.Curiosity > baseline.Curiosity);
        Assert.True(vectors.Fatigue < baseline.Fatigue);
    }

    [Fact]
    public async Task WhenReflectionReportsUnmappedMood_DoesNotWriteDriveVectors()
    {
        var (agent, _, _, store) = Create();
        var reflected = Envelope.Create(Topics.SystemControl, "Reflection", Severity.Neutral,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ReflectionAgent.ReflectedKind)
                .With(ReflectionAgent.MoodKey, "ecstatic"));

        await agent.HandleAsync(reflected, CancellationToken.None);

        Assert.Empty(await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None));
    }

    [Fact]
    public async Task WhenReflectionReportsNoMood_DoesNotWriteDriveVectors()
    {
        var (agent, _, _, store) = Create();
        var reflected = Envelope.Create(Topics.SystemControl, "Reflection", Severity.Neutral,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ReflectionAgent.ReflectedKind));

        await agent.HandleAsync(reflected, CancellationToken.None);

        Assert.Empty(await store.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, CancellationToken.None));
    }

    /// <summary>
    /// The one invariant that keeps slow colouring meaningfully distinct from
    /// the instant somatic shortcut. Asserted against the instant nudges
    /// themselves rather than a hard-coded ceiling, so tuning either side
    /// keeps the guarantee instead of silently invalidating the test.
    /// </summary>
    [Fact]
    public void EverySlowColoringDelta_IsSmallerThanEveryInstantNudge()
    {
        static double Largest(DriveVectors v) =>
            Math.Max(Math.Abs(v.Curiosity), Math.Max(Math.Abs(v.Fatigue),
                Math.Max(Math.Abs(v.Urgency), Math.Max(Math.Abs(v.SocialDrive), Math.Abs(v.Temperature)))));

        var smallestInstant = ImpulseAgent.InstantNudges.Min(Largest);
        foreach (var (label, delta) in ImpulseAgent.SlowColoringDeltas)
        {
            Assert.True(Largest(delta) < smallestInstant,
                $"slow-colouring delta '{label}' is not smaller than the smallest instant nudge");
        }
    }

    /// <summary>
    /// The advisory carries the face, and it carries the face this turn just
    /// produced — an urgent turn nudges urgency up, and the appraisal that
    /// reaches the surface has to reflect that, not the state before it.
    /// </summary>
    [Fact]
    public async Task AdvisoryCarriesTheAppraisedExpression()
    {
        var (agent, advisories, _, _) = Create();

        await agent.HandleAsync(Perceive("nothing much", null), CancellationToken.None);
        Assert.True(advisories.TryRead(out var calm));
        Assert.Equal("neutral", calm!.Meta.Get<string>(ImpulseAgent.ExpressionKey));

        await agent.HandleAsync(Perceive("emergency, need help now", null), CancellationToken.None);
        Assert.True(advisories.TryRead(out var urgent));
        Assert.Equal("alert", urgent!.Meta.Get<string>(ImpulseAgent.ExpressionKey));
    }

    /// <summary>
    /// Warmth and alertness can both be raised — a warm relationship in the
    /// middle of something urgent. The urgent face has to win, or the
    /// persona smiles through an emergency.
    /// </summary>
    [Fact]
    public async Task UrgencyOutranksWarmth()
    {
        var (agent, advisories, _, _) = Create();

        await agent.HandleAsync(Perceive("thanks, great job", null), CancellationToken.None);
        await agent.HandleAsync(Perceive("thanks, great job", null), CancellationToken.None);
        while (advisories.TryRead(out _)) { }

        await agent.HandleAsync(Perceive("emergency, need help now", null), CancellationToken.None);
        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("alert", advisory!.Meta.Get<string>(ImpulseAgent.ExpressionKey));
    }
}
