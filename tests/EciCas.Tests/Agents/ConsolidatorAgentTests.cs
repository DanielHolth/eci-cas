using EciCas.Agents.Consolidator;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
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

    private static IOptions<AgentSubstrateManifest> ManifestWith(bool useSubstrate) =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Consolidator"] = new AgentSubstrateEntry { Class = "fast-low", UseSubstrate = useSubstrate } } });

    [Fact]
    public async Task FlushesAndAnnouncesWritten_AtBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            new MockSubstrateProvider(), ManifestWith(useSubstrate: false), Options.Create(new ConsolidatorOptions { BatchSize = 2 }));

        var first = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.False(control.TryRead(out _));

        var second = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn two"));
        await agent.HandleAsync(second, CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ConsolidatorAgent.WrittenKind, written!.Meta.Get<string>(ConsolidatorAgent.ControlKindKey));

        var records = await store.LookupAsync(["turn"], maxPerPath: 10, CancellationToken.None);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task WritesUnderSignificantWordPaths_SoRecallCanFindThem()
    {
        // Regression for the M4 loop being silently broken: Reasoning only
        // proposes lookup paths for 5+ letter words, so Consolidator must
        // write under those same paths, not just the fixed "turn" category.
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            new MockSubstrateProvider(), ManifestWith(useSubstrate: false), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "What did we plan for dinner?"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var proposedPaths = SignificantWords.Extract("What did we plan for dinner?");
        Assert.Contains("dinner", proposedPaths);

        var records = await store.LookupAsync(proposedPaths, maxPerPath: 10, CancellationToken.None);
        Assert.Single(records);
        Assert.Equal("What did we plan for dinner?", records[0].Content);
    }

    [Fact]
    public async Task WhenUseSubstrateIsTrue_AddsExtractedFactToBatch()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("person/family/marcus: birth_date = 2020-08-28", TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, ManifestWith(useSubstrate: true), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "our son's birthday was yesterday"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var records = await store.LookupAsync(["person/family/marcus"], maxPerPath: 10, CancellationToken.None);
        Assert.Single(records);
        Assert.Equal("birth_date = 2020-08-28", records[0].Content);
    }

    [Fact]
    public async Task WhenSubstrateCallFails_DeterministicWriteStillLands()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, ManifestWith(useSubstrate: true), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var records = await store.LookupAsync(["turn"], maxPerPath: 10, CancellationToken.None);
        Assert.Single(records);
    }
}
