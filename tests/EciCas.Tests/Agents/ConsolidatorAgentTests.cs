using EciCas.Agents.Consolidator;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ConsolidatorAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Consolidator"] = new AgentSubstrateEntry { Class = "fast-low" } } });

    private const string FactLine = "category=person topic=family subtopic=son subject=marcus holth key=birthdate value=2020-08-28";

    [Fact]
    public async Task FlushesAndAnnouncesWritten_AtBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 2 }));

        var first = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.False(control.TryRead(out _));

        var second = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn two"));
        await agent.HandleAsync(second, CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ConsolidatorAgent.WrittenKind, written!.Meta.Get<string>(ConsolidatorAgent.ControlKindKey));

        var records = await store.LookupAsync(new ArchivePair("person", "family"), CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("2020-08-28", r.Value));
    }

    [Fact]
    public async Task WhenNothingExplicitlyStated_WritesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "tell me about your system"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.Empty(store.Index);
    }

    [Fact]
    public async Task WhenUseSubstrateIsTrue_AddsExtractedFactToBatch_WithScoredImportance()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "our son's birthday was yesterday"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var records = await store.LookupAsync(new ArchivePair("person", "family"), CancellationToken.None);
        Assert.Single(records);
        Assert.Equal("marcus holth", records[0].Subject);
        Assert.Equal(0.6, records[0].Importance);
    }

    [Fact]
    public async Task WhenSubstrateCallFails_WritesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.Empty(store.Index);
    }

    [Fact]
    public async Task WhenTriggeredBySelf_HardSkips_NeverCallsSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)); });

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "whether the trip dates still work")
                .With(ReflectionAgent.TriggeredByKey, "self"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.False(called);
        Assert.Empty(store.Index);
    }
}
