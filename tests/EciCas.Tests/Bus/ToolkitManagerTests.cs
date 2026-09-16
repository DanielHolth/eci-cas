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
    public async Task Manager_ReportsAFinishedRunOnTheNextPerceptionTurn_NotItsOwnCorrelation()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        var manager = Manager(bus, activity);
        await manager.StartAsync(CancellationToken.None);

        var request = Envelope.Create(Topics.ToolkitRequest, "ToolkitManager", Severity.Neutral, MetaBag.Empty);
        var result = request.Derive(Topics.ToolkitResult, "ToolkitHandler", Severity.Neutral,
            ToolkitResult.Build("powershell", "echo hi", "hello world", true));

        bus.Publish(Topics.ToolkitResult, result);
        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));

        // A result alone must not publish anything -- it only records state.
        Assert.False(advisories.TryRead(out _));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "some unrelated next turn"));
        bus.Publish(Topics.Perception, perception);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var advisory = await advisories.ReadAsync(cts.Token);

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(perception.CorrelationId, advisory.CorrelationId);
        Assert.NotEqual(result.CorrelationId, advisory.CorrelationId);
        Assert.Equal("powershell", advisory.Meta.Get<string>(ToolkitResult.NameKey));
        Assert.Contains("hello world", advisory.Meta.Get<string>(ToolkitManagerAgent.AdviceKey));
    }

    [Fact]
    public async Task Manager_DisabledTier_NeverPublishesAnAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        var manager = Manager(bus, activity, new ToolkitOptions { Enabled = false });
        await manager.StartAsync(CancellationToken.None);

        var result = Envelope.Create(Topics.ToolkitResult, "ToolkitHandler", Severity.Neutral,
            ToolkitResult.Build("powershell", "echo hi", "hello world", true));
        bus.Publish(Topics.ToolkitResult, result);
        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "hello"));
        bus.Publish(Topics.Perception, perception);
        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));

        await manager.StopAsync(CancellationToken.None);

        Assert.False(advisories.TryRead(out _));
    }
}
