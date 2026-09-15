using System.Diagnostics;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Receives toolkit requests and executes a tool in-process. For now this is a
/// PowerShell command runner used by the preview UI, and it reports its result on
/// the toolkit result topic so a manager can surface it back as perception.
/// </summary>
public sealed class ToolkitHandlerAgent : AgentBase
{
    private readonly IMessageBus _bus;

    public ToolkitHandlerAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ToolkitHandlerAgent> logger)
        : base(bus, activity, logger)
    {
        _bus = bus;
    }

    public override string Name => "ToolkitHandler";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.ToolkitRequest];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var command = envelope.Meta.Get<string>("toolkit.command") ?? "echo hello world";
        var toolName = envelope.Meta.Get<string>("toolkit.name") ?? "powershell";

        var result = RunTool(toolName, command);
        var response = Envelope.Create(
            Topics.ToolkitResult,
            Name,
            envelope.Severity,
            ToolkitResult.Build(toolName, command, result.output, result.success, result.error));

        response = response with { CorrelationId = envelope.CorrelationId };
        _bus.Publish(Topics.ToolkitResult, response);
        return Task.CompletedTask;
    }

    private static (string output, bool success, string? error) RunTool(string name, string command)
    {
        if (!string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, false, $"Unsupported toolkit '{name}'.");
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{Escape(command)}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return (string.Empty, false, "Failed to start PowerShell.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var finalOutput = output.Trim();
            if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
            {
                return (finalOutput, false, error.Trim());
            }

            return (finalOutput, process.ExitCode == 0, string.IsNullOrWhiteSpace(error) ? null : error.Trim());
        }
        catch (Exception ex)
        {
            return (string.Empty, false, ex.Message);
        }
    }

    private static string Escape(string command) => command.Replace("\"", "`\"");
}
