using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Listens to toolkit execution results and republishes them as a perception event
/// so the rest of the turn pipeline can vocalize the result through Morrow.
/// </summary>
public sealed class ToolkitManagerAgent : AgentBase
{
    private readonly IMessageBus _bus;

    public ToolkitManagerAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ToolkitManagerAgent> logger)
        : base(bus, activity, logger)
    {
        _bus = bus;
    }

    public override string Name => "ToolkitManager";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.ToolkitResult];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var name = envelope.Meta.Get<string>(ToolkitResult.NameKey) ?? "toolkit";
        var output = envelope.Meta.Get<string>(ToolkitResult.OutputKey) ?? string.Empty;
        var success = envelope.Meta.Get<bool>(ToolkitResult.SuccessKey);
        var error = envelope.Meta.Get<string>(ToolkitResult.ErrorKey);

        var perceptionText = string.IsNullOrWhiteSpace(output)
            ? success
                ? $"{name} completed successfully."
                : $"{name} failed. {error ?? "No output returned."}"
            : success
                ? $"{name} reported: {output}"
                : $"{name} failed: {error ?? output}";

        var toolkitEnvelope = Envelope.Create(
            Topics.PerceptionToolkit,
            Name,
            Severity.Neutral,
            MetaBag.Empty
                .With(PerceptionAgent.TextKey, perceptionText)
                .With("toolkit.output", output)
                .With("toolkit.success", success)
                .With("toolkit.name", name));

        var spokenEnvelope = Envelope.Create(
            Topics.Perception,
            Name,
            Severity.Neutral,
            MetaBag.Empty
                .With(PerceptionAgent.TextKey, perceptionText)
                .With("toolkit.output", output)
                .With("toolkit.success", success)
                .With("toolkit.name", name));

        _bus.Publish(Topics.PerceptionToolkit, toolkitEnvelope);
        _bus.Publish(Topics.Perception, spokenEnvelope);
        return Task.CompletedTask;
    }
}
