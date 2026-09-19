using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Receives toolkit requests and executes a tool in-process, dispatching by
/// name onto whichever <see cref="IToolkit"/> is registered for it -- see
/// <see cref="Startup.ToolkitRegistration"/> for the roster. Reports its
/// result on the toolkit result topic so a manager can surface it back as
/// perception.
///
/// Fully async end to end: the toolkit itself is what may run for seconds
/// (a PowerShell script), and nothing here blocks the dispatch thread while
/// it does. One worker, so requests to the same toolkit still queue behind
/// each other -- fine today at one request at a time, and the seam to raise
/// <see cref="AgentBase.WorkerCount"/> if that ever needs to change.
/// </summary>
public sealed class ToolkitHandlerAgent : AgentBase
{
    private readonly IMessageBus _bus;
    private readonly IReadOnlyDictionary<string, IToolkit> _toolkits;
    private readonly ToolkitOptions _options;

    public ToolkitHandlerAgent(
        IMessageBus bus,
        IEnumerable<IToolkit> toolkits,
        IOptions<ToolkitOptions> options,
        BusActivityTracker activity,
        ILogger<ToolkitHandlerAgent> logger)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _toolkits = toolkits.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
    }

    public override string Name => "ToolkitHandler";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.ToolkitRequest];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var command = envelope.Meta.Get<string>("toolkit.command") ?? "echo hello world";
        var toolName = envelope.Meta.Get<string>("toolkit.name") ?? "powershell";

        var (output, success, error, references) = await RunToolAsync(toolName, command, cancellationToken).ConfigureAwait(false);

        var response = Envelope.Create(
            Topics.ToolkitResult,
            Name,
            envelope.Severity,
            ToolkitResult.Build(toolName, command, output, success, error, references));

        response = response with { CorrelationId = envelope.CorrelationId };
        _bus.Publish(Topics.ToolkitResult, response);
    }

    private async Task<(string output, bool success, string? error, IReadOnlyList<ToolkitReference>? references)> RunToolAsync(
        string name, string command, CancellationToken cancellationToken)
    {
        if (!_toolkits.TryGetValue(name, out var toolkit))
        {
            return (string.Empty, false, $"Unsupported toolkit '{name}'.", null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var outcome = await toolkit.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
            return (outcome.Output, outcome.Success, outcome.Error, outcome.References);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout fired, not the host shutting down -- a real,
            // reportable outcome, not an exception for ConsumeAsync's catch
            // block to log and move past.
            return (string.Empty, false, $"Timed out after {_options.TimeoutSeconds}s.", null);
        }
        catch (Exception ex)
        {
            return (string.Empty, false, ex.Message, null);
        }
    }
}
