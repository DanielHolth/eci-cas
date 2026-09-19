using EciCas.Agents.Perception;
using EciCas.Agents.Toolkit;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Bus;

public class ToolkitManagerTests
{
    private sealed class NoEmbedder : IEmbeddingProvider
    {
        public bool Available => false;
        public string ModelId => "none";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>([]);
    }

    private static ToolkitManagerAgent Manager(IMessageBus bus, BusActivityTracker activity, ToolkitOptions? options = null) =>
        new(bus, new ToolkitCatalog([]), new NoEmbedder(), Options.Create(options ?? new ToolkitOptions()), activity, NullLogger<ToolkitManagerAgent>.Instance);

    [Fact]
    public async Task Manager_ReportsAFinishedRunAsAToolkitTriggeredPerception()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);

        var manager = Manager(bus, activity);
        await manager.StartAsync(CancellationToken.None);

        var hit = new ToolkitReference("Title", "https://example.com", "What it says");
        var request = Envelope.Create(Topics.ToolkitRequest, "ToolkitManager", Severity.Neutral, MetaBag.Empty);
        var result = request.Derive(Topics.ToolkitResult, "ToolkitHandler", Severity.Neutral,
            ToolkitResult.Build("search", "who is the king", "1. Title -- What it says", true, references: [hit]));
        bus.Publish(Topics.ToolkitResult, result);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var report = await perceptions.ReadAsync(cts.Token);
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(ToolkitManagerAgent.ToolkitTrigger, report.Meta.Get<string>(EciCas.Agents.Reflection.ReflectionAgent.TriggeredByKey));
        Assert.True(PerceptionAgent.IsBackground(report));
        Assert.NotEqual(result.CorrelationId, report.CorrelationId);
        Assert.Equal(1, report.Generation);
        Assert.Contains("who is the king", report.Meta.Get<string>(PerceptionAgent.TextKey));
        Assert.Contains("What it says", report.Meta.Get<string>(PerceptionAgent.TextKey));
        Assert.Equal([hit], report.Meta.Get<IReadOnlyList<ToolkitReference>>(ToolkitResult.ReferencesKey));
    }

    [Fact]
    public async Task Manager_DisabledTier_NeverReportsARun()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perceptions = bus.Subscribe(Topics.Perception);

        var manager = Manager(bus, activity, new ToolkitOptions { Enabled = false });
        await manager.StartAsync(CancellationToken.None);

        var result = Envelope.Create(Topics.ToolkitResult, "ToolkitHandler", Severity.Neutral,
            ToolkitResult.Build("powershell", "echo hi", "hello world", true));
        bus.Publish(Topics.ToolkitResult, result);
        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));

        await manager.StopAsync(CancellationToken.None);

        Assert.False(perceptions.TryRead(out _));
    }

    [Fact]
    public async Task Manager_OrdinaryTurn_TellsIntentWhatToolkitsItHas()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var catalog = new ToolkitCatalog([new ToolkitDescriptor("search", "Searches the web for current information -- news, prices.", ["Search the web."])]);
        var manager = new ToolkitManagerAgent(bus, catalog, new NoEmbedder(), Options.Create(new ToolkitOptions()), activity, NullLogger<ToolkitManagerAgent>.Instance);
        await manager.StartAsync(CancellationToken.None);

        bus.Publish(Topics.Perception, Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "hello there")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var advisory = await advisories.ReadAsync(cts.Token);
        await manager.StopAsync(CancellationToken.None);

        var advice = advisory.Meta.Get<string>(ToolkitManagerAgent.AdviceKey);
        Assert.Contains("search (Searches the web for current information)", advice);
        Assert.DoesNotContain("news, prices", advice);
    }
}
