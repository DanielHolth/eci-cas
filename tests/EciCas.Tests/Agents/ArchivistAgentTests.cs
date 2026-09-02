using EciCas.Agents.Archivist;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ArchivistAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Archivist"] = new AgentSubstrateEntry { Class = "fast-low" } } });

    private const string FactLine = "category=person topic=family subtopic=son subject=marcus holth key=birthdate value=2020-08-28";

    [Fact]
    public async Task FlushesAndAnnouncesWritten_AtBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 2 }));

        var first = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.False(control.TryRead(out _));

        var second = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn two"));
        await agent.HandleAsync(second, CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ArchivistAgent.WrittenKind, written!.Meta.Get<string>(ArchivistAgent.ControlKindKey));

        var records = await store.LookupAsync(new ArchivePair("person", "family"), null, CancellationToken.None);
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

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "tell me about your system"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.Empty(store.IndexFor(null));
    }

    [Fact]
    public async Task WhenUseSubstrateIsTrue_AddsExtractedFactToBatch_WithScoredImportance()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "our son's birthday was yesterday"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var records = await store.LookupAsync(new ArchivePair("person", "family"), null, CancellationToken.None);
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

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.Empty(store.IndexFor(null));
    }

    [Fact]
    public async Task SubtopicWithoutTopic_ParsesInsteadOfThrowing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "category=person subtopic=daughter subject=maia key=nickname value=benita", TimeSpan.Zero, 10, 0m)));

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "maia calls herself benita at school"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var records = await store.LookupAsync(new ArchivePair("person", "general"), null, CancellationToken.None);
        var record = Assert.Single(records);
        Assert.Equal("daughter", record.Subtopic);
        Assert.Equal("benita", record.Value);
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

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "whether the trip dates still work")
                .With(ReflectionAgent.TriggeredByKey, "self"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.False(called);
        Assert.Empty(store.IndexFor(null));
    }

    /// <summary>
    /// A batch can span turns and speakers, so the profile has to be kept per
    /// record rather than read off whichever envelope happens to trigger the
    /// flush.
    /// </summary>
    [Fact]
    public async Task BatchedFactsKeepTheProfileThatStatedThem()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        var agent = new ArchivistAgent(bus, activity, NullLogger<ArchivistAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ArchivistOptions { BatchSize = 2 }));

        foreach (var profileId in new[] { "daniel", "ada" })
        {
            await agent.HandleAsync(Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
                MetaBag.Empty.With(PerceptionAgent.TextKey, "a turn").With(PerceptionAgent.ProfileKey, profileId)),
                CancellationToken.None);
        }

        Assert.Equal(["ada", "daniel"], store.Scoped.Select(r => r.ProfileId).Order());
    }
}
