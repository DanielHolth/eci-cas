using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ReasoningAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Reasoning"] = new AgentSubstrateEntry { Class = "fast-medium" } } });

    [Fact]
    public async Task PublishesSelectedPairs_FromSubstrateChoice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);
        var store = new InMemoryArchiveStore();
        // Index deliberately wider than the cap: at or below it, Reasoning
        // skips the call and passes everything, which is a different test.
        await store.WriteAsync([
            new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow),
            .. Enumerable.Range(0, 3).Select(i =>
                new ArchiveRecord("world", $"topic{i}", "misc", "misc", "key", "value", DateTimeOffset.UtcNow))],
            null, CancellationToken.None);

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions { MaxSelectedPairs = 3 }),
            new StubEmbeddings(), new InMemoryPassageStore(), Options.Create(new PassageOptions()));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Equal(perception.CorrelationId, selection!.CorrelationId);
        var pairs = selection.Meta.Get<IReadOnlyList<ArchivePair>>(ReasoningAgent.SelectedPairsKey);
        Assert.Equal(new ArchivePair("person", "family"), Assert.Single(pairs!));
    }

    [Fact]
    public async Task WhenIndexIsEmpty_PublishesEmptySelection_WithoutCallingSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);
        var store = new InMemoryArchiveStore();
        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)); });
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions()),
            new StubEmbeddings(), new InMemoryPassageStore(), Options.Create(new PassageOptions()));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "anything on file?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Empty(selection!.Meta.Get<IReadOnlyList<ArchivePair>>(ReasoningAgent.SelectedPairsKey)!);
        Assert.False(called);
    }

    [Fact]
    public async Task WhenSubstrateCallFails_PublishesEmptySelection()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);
        // Four pairs against a cap of three: enough that the index does not
        // fit under the cap, so the selection call is actually attempted.
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([.. Enumerable.Range(0, 4).Select(i =>
            new ArchiveRecord("person", $"topic{i}", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow))],
            null, CancellationToken.None);

        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions()),
            new StubEmbeddings(), new InMemoryPassageStore(), Options.Create(new PassageOptions()));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Empty(selection!.Meta.Get<IReadOnlyList<ArchivePair>>(ReasoningAgent.SelectedPairsKey)!);
    }

    /// <summary>
    /// The cheapest selection call is the one not made. An index that already
    /// fits under the cap can only be narrowed by the model, never widened,
    /// and Recall filters row by row regardless — so passing it whole drops
    /// one of the turn's three serial substrate calls for nothing lost.
    /// </summary>
    [Fact]
    public async Task WhenTheWholeIndexFitsUnderTheCap_SkipsTheSubstrateCall()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([.. Enumerable.Range(0, 3).Select(i =>
            new ArchiveRecord("person", $"topic{i}", "son", "marcus", "birthdate", "2020-08-28", DateTimeOffset.UtcNow))],
            null, CancellationToken.None);

        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)); });
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions()),
            new StubEmbeddings(), new InMemoryPassageStore(), Options.Create(new PassageOptions()));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?")), CancellationToken.None);

        Assert.False(called);
        Assert.True(selections.TryRead(out var selection));
        Assert.Equal(3, selection!.Meta.Get<IReadOnlyList<ArchivePair>>(ReasoningAgent.SelectedPairsKey)!.Count);
    }
}
