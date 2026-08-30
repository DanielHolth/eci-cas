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
    public async Task PublishesSelectedTriples_FromSubstrateChoice()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedTriples);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow)], CancellationToken.None);

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions { MaxSelectedTriples = 3 }));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Equal(perception.CorrelationId, selection!.CorrelationId);
        var triples = selection.Meta.Get<IReadOnlyList<ArchiveTriple>>(ReasoningAgent.SelectedTriplesKey);
        Assert.Equal(new ArchiveTriple("person", "family", "son"), Assert.Single(triples!));
    }

    [Fact]
    public async Task WhenIndexIsEmpty_PublishesEmptySelection_WithoutCallingSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedTriples);
        var store = new InMemoryArchiveStore();
        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)); });
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions()));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "anything on file?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Empty(selection!.Meta.Get<IReadOnlyList<ArchiveTriple>>(ReasoningAgent.SelectedTriplesKey)!);
        Assert.False(called);
    }

    [Fact]
    public async Task WhenSubstrateCallFails_PublishesEmptySelection()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedTriples);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow)], CancellationToken.None);

        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));
        var agent = new ReasoningAgent(bus, activity, NullLogger<ReasoningAgent>.Instance, store, substrate,
            Manifest(), Options.Create(new ReasoningOptions()));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.Empty(selection!.Meta.Get<IReadOnlyList<ArchiveTriple>>(ReasoningAgent.SelectedTriplesKey)!);
    }
}
