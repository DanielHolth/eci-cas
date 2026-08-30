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

    private static Envelope Selection(params ArchiveTriple[] triples) =>
        Envelope.Create(Topics.SelectedTriples, "Reasoning", Severity.Neutral,
            MetaBag.Empty.With(ReasoningAgent.SelectedTriplesKey, (IReadOnlyList<ArchiveTriple>)triples));

    [Fact]
    public async Task PublishesAdvisory_WithPickedFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var triple = new ArchiveTriple("person", "family", "son");
        await store.WriteAsync([new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow)], CancellationToken.None);

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(triple), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey);
        Assert.Single(facts!);
        Assert.Equal("2020-08-28", facts![0].Value);
    }

    [Fact]
    public async Task WhenNoTriplesSelected_PublishesEmptyAdvisory_WithoutCallingSubstrate()
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
    public async Task WhenTripleHasNoCandidates_SkipsSubstrateCallForThatTriple()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0", TimeSpan.Zero, 5, 0m)));
        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(), Options.Create(new RecallOptions()));

        await agent.HandleAsync(Selection(new ArchiveTriple("person", "family", "son")), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Empty(advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!);
    }

    [Fact]
    public async Task WhenSubstrateCallFailsForATriple_ContributesNothingButOtherTriplesStillSurface()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        var failing = new ArchiveTriple("person", "family", "son");
        var working = new ArchiveTriple("event", "wedding", "family");
        await store.WriteAsync([
            new ArchiveRecord("person", "family", "son", "marcus holth", "birthdate", "2020-08-28", DateTimeOffset.UtcNow),
            new ArchiveRecord("event", "wedding", "family", "maria holth", "location", "drammen kirke", DateTimeOffset.UtcNow),
        ], CancellationToken.None);

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
}
