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
}
