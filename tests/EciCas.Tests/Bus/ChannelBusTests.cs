using System.Diagnostics;
using EciCas.Bus;
using EciCas.Core;

namespace EciCas.Tests.Bus;

public class ChannelBusTests
{
    [Fact]
    public async Task Publish_WithSlowSubscriber_ReturnsImmediately()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var reader = bus.Subscribe(Topics.Perception);

        var consumer = Task.Run(async () =>
        {
            var envelope = await reader.ReadAsync();
            await Task.Delay(500);
            activity.OnHandled();
        });

        var stopwatch = Stopwatch.StartNew();
        bus.Publish(Topics.Perception, Envelope.Create(Topics.Perception, "Test", Severity.Neutral));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 5, $"Publish took {stopwatch.ElapsedMilliseconds}ms — a slow subscriber must never block the publisher.");

        await consumer;
    }

    /// <summary>
    /// ChannelBus has no replay: a publish with no subscriber on the topic is
    /// dropped on the floor, no error raised. So the moment an agent claims
    /// its queue is a correctness property, not a detail — claiming it inside
    /// ExecuteAsync (which BackgroundService runs *after* StartAsync returns)
    /// loses every envelope published in that window.
    /// </summary>
    [Fact]
    public async Task StartAsync_SubscribesBeforeReturning()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var agent = new RecordingAgent(bus, activity);

        await agent.StartAsync(CancellationToken.None);

        // Published the instant StartAsync returns — before the consumer loop
        // has necessarily been scheduled. The queue must already exist.
        bus.Publish(Topics.Perception, Envelope.Create(Topics.Perception, "Test", Severity.Neutral));

        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));
        await agent.StopAsync(CancellationToken.None);

        Assert.Single(agent.Received);
    }

    private sealed class RecordingAgent : AgentBase
    {
        public List<Envelope> Received { get; } = [];

        public RecordingAgent(IMessageBus bus, BusActivityTracker activity)
            : base(bus, activity, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
        }

        public override string Name => "Recording";
        public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

        public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            lock (Received)
            {
                Received.Add(envelope);
            }

            return Task.CompletedTask;
        }
    }
}
