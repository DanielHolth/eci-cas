using EciCas.Agents.Toolkit;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Bus;

public class ToolkitHandlerTests
{
    private sealed class FakeToolkit(string name, Func<string, CancellationToken, Task<ToolkitOutcome>> execute) : IToolkit
    {
        public string Name { get; } = name;
        public Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken) =>
            execute(command, cancellationToken);
    }

    private static async Task<Envelope> RunAndCaptureResultAsync(
        IEnumerable<IToolkit> toolkits, ToolkitOptions options, string toolName, string command)
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var handler = new ToolkitHandlerAgent(
            bus, toolkits, Options.Create(options), activity, NullLogger<ToolkitHandlerAgent>.Instance);

        var reader = bus.Subscribe(Topics.ToolkitResult);
        await handler.StartAsync(CancellationToken.None);

        bus.Publish(Topics.ToolkitRequest, Envelope.Create(
            Topics.ToolkitRequest,
            "test",
            Severity.Neutral,
            ToolkitRequest.Build(toolName, command)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captured = await reader.ReadAsync(cts.Token);
        await handler.StopAsync(CancellationToken.None);

        return captured;
    }

    [Fact]
    public async Task Handler_DispatchesToTheMatchingToolkitByName()
    {
        var powershell = new FakeToolkit("powershell", (cmd, _) => Task.FromResult(new ToolkitOutcome(cmd + "!", true, null)));

        var result = await RunAndCaptureResultAsync([powershell], new ToolkitOptions(), "powershell", "hello");

        Assert.Equal("hello!", result.Meta.Get<string>(ToolkitResult.OutputKey));
        Assert.True(result.Meta.Get<bool>(ToolkitResult.SuccessKey));
    }

    [Fact]
    public async Task Handler_ReportsUnsupportedToolkitInsteadOfThrowing()
    {
        var result = await RunAndCaptureResultAsync([], new ToolkitOptions(), "guide", "do the thing");

        Assert.False(result.Meta.Get<bool>(ToolkitResult.SuccessKey));
        Assert.Contains("Unsupported toolkit", result.Meta.Get<string>(ToolkitResult.ErrorKey));
    }

    [Fact]
    public async Task Handler_CancelsAToolkitThatOutlivesTheTimeoutBudget()
    {
        var hangs = new FakeToolkit("hangs", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolkitOutcome("unreachable", true, null);
        });

        var result = await RunAndCaptureResultAsync(
            [hangs], new ToolkitOptions { TimeoutSeconds = 1 }, "hangs", "sleep forever");

        Assert.False(result.Meta.Get<bool>(ToolkitResult.SuccessKey));
        Assert.Contains("Timed out", result.Meta.Get<string>(ToolkitResult.ErrorKey));
    }
}
