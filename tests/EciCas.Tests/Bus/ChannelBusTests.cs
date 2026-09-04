using EciCas.Bus;
using EciCas.Core;

namespace EciCas.Tests.Bus;

public class ChannelBusTests
{
    /// <summary>
    /// The rule the whole architecture rests on: Publish() never waits on a
    /// subscriber. Proven by construction rather than by the clock — the
    /// consumer takes the envelope and then parks on a gate that only this
    /// test can open, and it opens it after Publish has already returned. A
    /// bus that waited on its subscribers could not reach that line, so the
    /// failure is a hang, and Timeout turns the hang into a red test.
    ///
    /// It used to assert "under 5 ms", which measured the machine as much as
    /// the bus: a cold JIT on the first run of a session could lose that race
    /// while the invariant held perfectly. Speed was never the property worth
    /// pinning — not blocking is.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task Publish_DoesNotWaitForASubscriberToFinishHandling()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var reader = bus.Subscribe(Topics.Perception);

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = Task.Run(async () =>
        {
            await reader.ReadAsync();
            received.SetResult();
            await release.Task;
            activity.OnHandled();
        });

        bus.Publish(Topics.Perception, Envelope.Create(Topics.Perception, "Test", Severity.Neutral));

        // Publish returned. Nothing else has been allowed to happen yet, so
        // this is the invariant itself and not a symptom of it.
        Assert.False(consumer.IsCompleted);

        await received.Task;
        release.SetResult();
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
