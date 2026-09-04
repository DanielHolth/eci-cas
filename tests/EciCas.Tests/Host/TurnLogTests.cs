using EciCas.Agents.Archivist;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host.TurnLog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Host;

public class TurnLogTests
{
    private static readonly ArchiveRecord Fact = new(
        "person", "family", "daniel", "daughter", "name", "vera",
        DateTimeOffset.UnixEpoch, ArchiveDomain.External, 0.8);

    private static Envelope Perception(string text, string? profileId = null) =>
        Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, text)
                .With(PerceptionAgent.ProfileKey, profileId));

    private static TurnRecord Project(params Envelope[] envelopes)
    {
        TurnRecord? record = null;
        foreach (var envelope in envelopes)
        {
            record = TurnProjection.Apply(record, envelope, 1);
        }

        return record!;
    }

    [Fact]
    public void Apply_WithEnvelopesOutOfOrder_FillsSlotsRatherThanAppending()
    {
        var perception = Perception("who is vera?");
        var record = Project(
            perception.Derive(Topics.Action, "Governance", Severity.Neutral,
                MetaBag.Empty.With(IntentAgent.ReplyKey, "Your daughter.").With(SecurityAgent.VerdictKey, Verdict.Green)),
            perception.Derive(Topics.Advisories, "Recall", Severity.Neutral,
                MetaBag.Empty.With(RecallAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)[Fact])),
            perception);

        Assert.Equal("who is vera?", record.Perception);
        Assert.Equal("person/family/daniel/daughter/name = vera", Assert.Single(record.Reads));
        Assert.Equal("Your daughter.", record.Intent);
        Assert.True(record.Concluded);
    }

    [Fact]
    public void Apply_WithGreenVerdict_LeavesVerdictUnreported()
    {
        var perception = Perception("hello");
        var record = Project(perception,
            perception.Derive(Topics.Verdict, "Security", Severity.Neutral,
                MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Green)));

        Assert.Null(record.Verdict);
    }

    [Fact]
    public void Apply_WithRedVerdict_CarriesTheConcern()
    {
        var perception = Perception("do something reckless");
        var record = Project(perception,
            perception.Derive(Topics.Verdict, "Security", Severity.Critical,
                MetaBag.Empty.With(SecurityAgent.VerdictKey, Verdict.Red).With(SecurityAgent.ConcernKey, "self-harm")));

        Assert.Equal("red", record.Verdict);
        Assert.Equal("self-harm", record.Concern);
    }

    [Fact]
    public void Apply_WithSelfTriggeredPerception_DoesNotReadAsSomethingAPersonSaid()
    {
        var idea = Envelope.Create(Topics.Perception, "Reflection", Severity.Restful,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "vera's name keeps coming up")
                .With(ReflectionAgent.TriggeredByKey, "self"));

        Assert.True(Project(idea).SelfTriggered);
        Assert.False(Project(Perception("hello")).SelfTriggered);
    }

    [Fact]
    public void Apply_WithTelemetry_SumsCostAndKeepsCallsSeparate()
    {
        var perception = Perception("who is vera?");
        var bus = new RecordingBus();
        SubstrateTrace.Publish(bus, perception, "Librarian", "fast-medium", new SubstrateResult("", TimeSpan.FromMilliseconds(120), 300, 0.0004m));
        SubstrateTrace.Publish(bus, perception, "Recall", "fast-low", new SubstrateResult("", TimeSpan.FromMilliseconds(80), 100, 0.0001m), "person/family");

        var record = Project([perception, .. bus.Published]);

        Assert.Equal(2, record.Calls.Count);
        Assert.Equal("person/family", record.Calls[1].Label);
        Assert.Equal(0.0005m, record.Cost);
    }

    [Fact]
    public void Apply_WithFailedCall_ReportsTheCauseAndNoCost()
    {
        var perception = Perception("who is vera?");
        var bus = new RecordingBus();
        SubstrateTrace.PublishFailure(bus, perception, "Intent", "fast-high", 15000, SubstrateHealth.TimedOut);

        var record = Project([perception, .. bus.Published]);

        Assert.Equal(SubstrateHealth.TimedOut, Assert.Single(record.Calls).Degraded);
        Assert.Null(record.Cost);
        Assert.Equal(15000, record.Calls[0].LatencyMs);
    }

    [Fact]
    public void Apply_WithControlEnvelopes_ShowsWhatWasWritten()
    {
        var perception = Perception("vera is six");
        var record = Project(perception,
            perception.Derive(Topics.SystemControl, "Archivist", Severity.Neutral,
                MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ArchivistAgent.WrittenKind)
                    .With(ArchivistAgent.WrittenRecordsKey, (IReadOnlyList<string>)["person/family/daniel/daughter/age = 6"])),
            perception.Derive(Topics.SystemControl, "Reflection", Severity.Neutral,
                MetaBag.Empty.With(ReflectionAgent.PassagesKey, (IReadOnlyList<string>)["ages come up often"])
                    .With(ReflectionAgent.IdeaKey, "ask about her birthday")));

        Assert.Equal("person/family/daniel/daughter/age = 6", Assert.Single(record.Writes));
        Assert.Equal("ages come up often", Assert.Single(record.Passages));
        Assert.Equal("ask about her birthday", record.Idea);
    }

    [Fact]
    public void Apply_WithHindsightNotes_CarriesThemWhole()
    {
        var perception = Perception("who is vera?");
        var record = Project(perception,
            perception.Derive(Topics.Advisories, "Hindsight", Severity.Neutral,
                MetaBag.Empty.With(HindsightAgent.NotesKey, (IReadOnlyList<string>)["asked this before"])),
            perception.Derive(Topics.Advisories, "Impulse", Severity.Neutral,
                MetaBag.Empty.With(ImpulseAgent.AdviceKey, "curious")));

        Assert.Equal("asked this before", Assert.Single(record.Hindsight));
        Assert.Equal("curious", record.Impulse);
    }

    [Fact]
    public async Task Subscriber_WhenAnEventGoesQuiet_HandsItToTheSinks()
    {
        var sink = new CapturingSink();
        var log = Subscriber(sink, settleMs: 30);

        await log.HandleAsync(Perception("hello"), CancellationToken.None);

        var settled = await sink.WaitAsync();
        Assert.Equal("hello", settled.Perception);
    }

    [Fact]
    public async Task Subscriber_WhileAnEventIsStillArriving_WritesNothingYet()
    {
        var sink = new CapturingSink();
        var log = Subscriber(sink, settleMs: 200);
        var perception = Perception("hello");

        await log.HandleAsync(perception, CancellationToken.None);
        await Task.Delay(120);
        await log.HandleAsync(perception.Derive(Topics.Action, "Governance", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, "hi")), CancellationToken.None);
        await Task.Delay(120);

        Assert.Empty(sink.Written);

        var settled = await sink.WaitAsync();
        Assert.Equal("hi", settled.Intent);
    }

    [Fact]
    public async Task Subscriber_ScopedToAProfile_SkipsAnotherPersonsEventButKeepsTheUnowned()
    {
        var log = Subscriber(new CapturingSink(), settleMs: 10_000);
        await log.HandleAsync(Perception("mine", "daniel"), CancellationToken.None);
        await log.HandleAsync(Perception("theirs", "vera"), CancellationToken.None);
        await log.HandleAsync(Envelope.Create(Topics.SystemControl, "Reflection", Severity.Neutral,
            MetaBag.Empty.With(ReflectionAgent.IdeaKey, "a thought")), CancellationToken.None);

        var seen = log.Recent("daniel");

        Assert.Equal(2, seen.Count);
        Assert.Equal("mine", seen[0].Perception);
        Assert.Equal("a thought", seen[1].Idea);
    }

    [Fact]
    public async Task Subscriber_BeyondRetain_ForgetsTheOldestEvent()
    {
        var log = Subscriber(new CapturingSink(), settleMs: 10_000, retain: 2);
        await log.HandleAsync(Perception("one"), CancellationToken.None);
        await log.HandleAsync(Perception("two"), CancellationToken.None);
        await log.HandleAsync(Perception("three"), CancellationToken.None);

        Assert.Equal(["two", "three"], log.Recent(null).Select(r => r.Perception));
    }

    private static TurnLogSubscriber Subscriber(ITurnLogSink sink, int settleMs, int retain = 100)
    {
        var activity = new BusActivityTracker();
        return new TurnLogSubscriber(new ChannelBus(activity), activity, NullLogger<TurnLogSubscriber>.Instance,
            Options.Create(new TurnLogOptions { SettleMs = settleMs, Retain = retain }), [sink]);
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<Envelope> Published { get; } = [];

        public void Publish(string topic, Envelope envelope) => Published.Add(envelope);

        public System.Threading.Channels.ChannelReader<Envelope> Subscribe(string topic) =>
            System.Threading.Channels.Channel.CreateUnbounded<Envelope>().Reader;
    }

    private sealed class CapturingSink : ITurnLogSink
    {
        private readonly TaskCompletionSource<TurnRecord> _first = new();

        public List<TurnRecord> Written { get; } = [];

        public Task WriteAsync(TurnRecord record, CancellationToken cancellationToken)
        {
            Written.Add(record);
            _first.TrySetResult(record);
            return Task.CompletedTask;
        }

        public async Task<TurnRecord> WaitAsync() => await _first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
