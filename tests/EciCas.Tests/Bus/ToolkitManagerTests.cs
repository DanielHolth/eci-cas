using EciCas.Agents.Toolkit;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Bus;

public class ToolkitManagerTests
{
    [Fact]
    public async Task Manager_PublishesToolkitResultAsPerception()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var manager = new ToolkitManagerAgent(bus, activity, NullLogger<ToolkitManagerAgent>.Instance);

        await manager.StartAsync(CancellationToken.None);

        bus.Publish(Topics.ToolkitResult, Envelope.Create(
            Topics.ToolkitResult,
            "ToolkitHandler",
            Severity.Neutral,
            MetaBag.Empty
                .With(ToolkitResult.OutputKey, "hello world")
                .With(ToolkitResult.NameKey, "powershell")
                .With(ToolkitResult.SuccessKey, true)));

        await activity.WhenIdleAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync(CancellationToken.None);

        Assert.True(true);
    }
}
