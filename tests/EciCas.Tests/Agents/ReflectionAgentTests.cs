using System.Text.Json;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ReflectionAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Reflection"] = new AgentSubstrateEntry { Class = "slow-low" } } });

    private static async Task SeedDriveVectorsAsync(IAgentStateStore stateStore, DriveVectors vectors) =>
        await stateStore.WriteAsync([new AgentStateRecord(ImpulseAgent.DrivePath, JsonSerializer.Serialize(vectors), DateTimeOffset.UtcNow, ArchiveDomain.Internal)], CancellationToken.None);

    private static Envelope Conclusion(string reply, int generation = 0) =>
        Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral,
            MetaBag.Empty.With(IntentAgent.ReplyKey, reply), generation: generation);

    [Fact]
    public async Task DoesNotFlush_BelowBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var stateStore = new JsonlAgentStateStore(Path.GetTempFileName());
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, store, stateStore, new MockSubstrateProvider(),
            Manifest(), Options.Create(new ReflectionOptions { BatchSize = 3 }));

        await agent.HandleAsync(Conclusion("tacos sound good"), CancellationToken.None);
        await agent.HandleAsync(Conclusion("maybe pizza instead"), CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));
        Assert.False(control.TryRead(out _));
    }

    [Fact]
    public async Task AtBatchSize_WithHighEagerness_PushesBestIdeaAndArchivesLosersInternally()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var stateStore = new JsonlAgentStateStore(Path.GetTempFileName());
        await SeedDriveVectorsAsync(stateStore, new DriveVectors(Curiosity: 0.9, Fatigue: 0.0));

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "0.9|hypothesis|whether the trip dates still work\n0.3|question|a minor follow-up thought", TimeSpan.Zero, 10, 0m)));
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, store, stateStore, substrate,
            Manifest(), Options.Create(new ReflectionOptions { BatchSize = 2, MaxIdeaGeneration = 1, EagernessThreshold = 0.6 }));

        await agent.HandleAsync(Conclusion("tacos sound good"), CancellationToken.None);
        await agent.HandleAsync(Conclusion("maybe pizza instead"), CancellationToken.None);

        Assert.True(perceptions.TryRead(out var idea));
        Assert.Equal("whether the trip dates still work", idea!.Meta.Get<string>(PerceptionAgent.TextKey));
        Assert.Equal("self", idea.Meta.Get<string>(ReflectionAgent.TriggeredByKey));
        Assert.Equal(1, idea.Generation);

        Assert.True(control.TryRead(out var reflected));
        Assert.Equal(ReflectionAgent.ReflectedKind, reflected!.Meta.Get<string>(EciCas.Agents.Consolidator.ConsolidatorAgent.ControlKindKey));

        var pushed = await store.LookupAsync(new ArchiveTriple("self", "reflection", "hypothesis"), maxRows: 10, CancellationToken.None);
        Assert.Contains(pushed, r => r.Value == "whether the trip dates still work" && r.Importance == 0.2);

        var quiet = await store.LookupAsync(new ArchiveTriple("self", "reflection", "question"), maxRows: 10, CancellationToken.None);
        Assert.Contains(quiet, r => r.Value == "a minor follow-up thought" && r.Domain == ArchiveDomain.Internal && r.Importance == 0.1);
    }

    [Fact]
    public async Task AtBatchSize_WithLowEagerness_WritesQuietlyAndDoesNotPush()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var store = new InMemoryArchiveStore();
        var stateStore = new JsonlAgentStateStore(Path.GetTempFileName());
        await SeedDriveVectorsAsync(stateStore, new DriveVectors(Curiosity: 0.1, Fatigue: 0.8));

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0.9|pattern|a compelling idea", TimeSpan.Zero, 10, 0m)));
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, store, stateStore, substrate,
            Manifest(), Options.Create(new ReflectionOptions { BatchSize = 1, MaxIdeaGeneration = 1, EagernessThreshold = 0.6 }));

        await agent.HandleAsync(Conclusion("tacos sound good"), CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));

        var records = await store.LookupAsync(new ArchiveTriple("self", "reflection", "pattern"), maxRows: 10, CancellationToken.None);
        Assert.Contains(records, r => r.Value == "a compelling idea" && r.Domain == ArchiveDomain.Internal && r.Importance == 0.1);
    }

    [Fact]
    public async Task AtGenerationCap_NeverPushes_EvenWithHighEagerness()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var store = new InMemoryArchiveStore();
        var stateStore = new JsonlAgentStateStore(Path.GetTempFileName());
        await SeedDriveVectorsAsync(stateStore, new DriveVectors(Curiosity: 0.9, Fatigue: 0.0));

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("0.9|pattern|a great idea", TimeSpan.Zero, 10, 0m)));
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, store, stateStore, substrate,
            Manifest(), Options.Create(new ReflectionOptions { BatchSize = 1, MaxIdeaGeneration = 1, EagernessThreshold = 0.6 }));

        await agent.HandleAsync(Conclusion("tacos sound good", generation: 1), CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));
    }

    [Fact]
    public async Task WhenSubstrateCallFails_SkipsFlush_PushesAndWritesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var stateStore = new JsonlAgentStateStore(Path.GetTempFileName());
        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));
        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, store, stateStore, substrate,
            Manifest(), Options.Create(new ReflectionOptions { BatchSize = 1 }));

        await agent.HandleAsync(Conclusion("tacos sound good"), CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));
        Assert.False(control.TryRead(out _));
        Assert.Empty(store.Index);
    }
}
