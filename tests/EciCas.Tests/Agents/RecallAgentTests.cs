using EciCas.Agents.Reasoning;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class RecallAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Recall"] = new AgentSubstrateEntry { Class = "fast-low" } } });

    private static Envelope Selection(params ArchivePair[] pairs) =>
        Envelope.Create(Topics.SelectedPairs, "Reasoning", Severity.Neutral,
            MetaBag.Empty.With(ReasoningAgent.SelectedPairsKey, (IReadOnlyList<ArchivePair>)pairs));

    [Fact]
    public async Task PublishesAdvisory_WithPickedFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var pair = new ArchivePair("person", "family");
        await store.WriteAsync([new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow)], null, CancellationToken.None);

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(pair), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey);
        Assert.Single(facts!);
        Assert.Equal("2020-08-28", facts![0].Value);
    }

    [Fact]
    public async Task WhenNoPairsSelected_PublishesEmptyAdvisory_WithoutCallingSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)); });
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Empty(advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!);
        Assert.False(called);
    }

    [Fact]
    public async Task WhenPairHasNoCandidates_SkipsSubstrateCallForThatPair()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(new ArchivePair("person", "family")), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Empty(advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!);
    }

    [Fact]
    public async Task WhenSubstrateCallFailsForOnePair_ContributesNothingButOtherPairsStillSurface()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var failing = new ArchivePair("person", "family");
        var working = new ArchivePair("event", "wedding");
        await store.WriteAsync([
            new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow),
            new ArchiveRecord("event", "wedding", "family", "maria holth", "location", "drammen kirke", DateTimeOffset.UtcNow),
        ], null, CancellationToken.None);

        var substrate = new StubSubstrate(prompt => prompt.Contains("drammen", StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m))
            : throw new InvalidOperationException("down"));
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(failing, working), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey);
        Assert.Single(facts!);
        Assert.Equal("drammen kirke", facts![0].Value);
    }

    private static ArchiveRecord Row(string category, string topic, int i) =>
        new(category, topic, "sub", "subject", $"key{i}", $"value {i}", DateTimeOffset.UtcNow);

    /// <summary>
    /// A pair holding more than one worker's worth of rows is split, not
    /// truncated — every row reaches some picking call.
    /// </summary>
    [Fact]
    public async Task PairLargerThanRowsPerWorker_IsSplitAcrossWorkers_WithNoRowDropped()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([.. Enumerable.Range(0, 25).Select(i => Row("science", "thermodynamics", i))], null, CancellationToken.None);

        var prompts = new List<string>();
        var substrate = new StubSubstrate(prompt =>
        {
            lock (prompts) { prompts.Add(prompt); }
            return Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 5, 0m));
        });
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(),
            Options.Create(new RecallOptions { RowsPerWorker = 10, MaxConcurrentRecalls = 10 }));

        await agent.HandleAsync(Selection(new ArchivePair("science", "thermodynamics")), CancellationToken.None);

        Assert.Equal(3, prompts.Count);
        for (var i = 0; i < 25; i++)
        {
            Assert.Contains(prompts, p => p.Contains($"value {i}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FanOutNeverExceedsMaxConcurrentRecalls()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([.. Enumerable.Range(0, 100).Select(i => Row("science", "thermodynamics", i))], null, CancellationToken.None);

        var calls = 0;
        var substrate = new StubSubstrate(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 5, 0m));
        });
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(),
            Options.Create(new RecallOptions { RowsPerWorker = 5, MaxConcurrentRecalls = 4 }));

        await agent.HandleAsync(Selection(new ArchivePair("science", "thermodynamics")), CancellationToken.None);

        Assert.Equal(4, calls);
    }

    /// <summary>
    /// The trim is breadth-first, so one deep pair can't spend the whole
    /// budget and leave a shallow pair unread — every selected pair gets at
    /// least its first (most important) chunk.
    /// </summary>
    [Fact]
    public async Task WhenTrimming_EverySelectedPairStillGetsItsFirstChunk()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([.. Enumerable.Range(0, 50).Select(i => Row("science", "thermodynamics", i))], null, CancellationToken.None);
        await store.WriteAsync([Row("person", "family", 999)], null, CancellationToken.None);

        var prompts = new List<string>();
        var substrate = new StubSubstrate(prompt =>
        {
            lock (prompts) { prompts.Add(prompt); }
            return Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 5, 0m));
        });
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(),
            Options.Create(new RecallOptions { RowsPerWorker = 5, MaxConcurrentRecalls = 3 }));

        await agent.HandleAsync(Selection(new ArchivePair("science", "thermodynamics"), new ArchivePair("person", "family")), CancellationToken.None);

        Assert.Equal(3, prompts.Count);
        Assert.Contains(prompts, p => p.Contains("value 999", StringComparison.Ordinal));
    }

    /// <summary>Subtopic left the address, so it has to reach the picking model as data.</summary>
    [Fact]
    public async Task PickingPromptShowsSubtopic()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow)], null, CancellationToken.None);

        var seen = string.Empty;
        var substrate = new StubSubstrate(prompt => { seen = prompt; return Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 5, 0m)); });
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(new ArchivePair("person", "family")), CancellationToken.None);

        Assert.Contains("son", seen, StringComparison.Ordinal);
    }
}
