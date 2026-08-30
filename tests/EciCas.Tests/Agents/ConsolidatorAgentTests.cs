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

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Consolidator"] = new AgentSubstrateEntry { Class = "fast-low" } } });

    [Fact]
    public async Task FlushesAndAnnouncesWritten_AtBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("person/family/marcus: birth_date = 2020-08-28", TimeSpan.Zero, 10, 0m)));

        // Each turn's fact dual-writes under its own path plus keyword
        // paths, so BatchSize (which counts records, not turns) needs
        // headroom for that — 3 stays below the 2-record first turn but
        // crosses at the 4-record second one.
        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 3 }));

        var first = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.False(control.TryRead(out _));

        var second = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn two"));
        await agent.HandleAsync(second, CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ConsolidatorAgent.WrittenKind, written!.Meta.Get<string>(ConsolidatorAgent.ControlKindKey));

        var records = await store.LookupAsync(["person/family/marcus"], maxPerPath: 10, CancellationToken.None);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task WhenNothingExplicitlyStated_WritesNothing()
    {
        // Matches the Python prototype's Consolidator: no deterministic
        // fallback write, so a low-content/meta turn with no LLM-extracted
        // fact yields zero archive records.
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "tell me about your system"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.False(File.Exists(path) && File.ReadAllLines(path).Any(l => !string.IsNullOrWhiteSpace(l)));
    }

    [Fact]
    public async Task WhenUseSubstrateIsTrue_AddsExtractedFactToBatch_ReachableByBothPaths()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("person/family/marcus: birth_date = 2020-08-28", TimeSpan.Zero, 10, 0m)));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "our son's birthday was yesterday"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        var byOwnPath = await store.LookupAsync(["person/family/marcus"], maxPerPath: 10, CancellationToken.None);
        Assert.Single(byOwnPath);
        Assert.Equal("person/family/marcus/birth_date = 2020-08-28", byOwnPath[0].Content);

        var byKeyword = await store.LookupAsync(SignificantWords.Extract("birth_date = 2020-08-28"), maxPerPath: 10, CancellationToken.None);
        Assert.Contains(byKeyword, r => r.Content == "person/family/marcus/birth_date = 2020-08-28");
    }

    [Fact]
    public async Task WhenSubstrateCallFails_WritesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);
        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            substrate, Manifest(), Options.Create(new ConsolidatorOptions { BatchSize = 1 }));

        var bundle = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(bundle, CancellationToken.None);

        Assert.False(control.TryRead(out _));
        Assert.False(File.Exists(path) && File.ReadAllLines(path).Any(l => !string.IsNullOrWhiteSpace(l)));
    }
}
