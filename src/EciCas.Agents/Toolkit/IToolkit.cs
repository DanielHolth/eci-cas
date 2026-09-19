using EciCas.Core;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// One executable tool, named the way a request names it in
/// <see cref="ToolkitRequest.NameKey"/>. <see cref="ToolkitHandlerAgent"/>
/// dispatches on <see cref="Name"/> rather than switching on a hardcoded
/// string, so a second toolkit (guide-toolkit, say) is a new class plus one
/// DI registration, not a branch inside an existing agent.
/// </summary>
public interface IToolkit
{
    /// <summary>Matched case-insensitively against a request's toolkit name.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the tool. Must honor <paramref name="cancellationToken"/> promptly
    /// -- the caller cancels it once the toolkit's own timeout budget (see
    /// <see cref="ToolkitOptions"/>) elapses, and a toolkit that ignores it
    /// blocks the handler's whole queue for the life of the process it started.
    /// </summary>
    Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken);
}

/// <summary>A toolkit's result, independent of the bus's own envelope shape.</summary>
public sealed record ToolkitOutcome(
    string Output,
    bool Success,
    string? Error,
    IReadOnlyList<ToolkitReference>? References = null,
    IReadOnlyList<ToolkitSubstrateCall>? Usage = null);

/// <summary>
/// One model call a toolkit made on its own account (PowerShell's translation
/// step). The handler publishes each as a <see cref="EciCas.Bus.SubstrateTrace"/>
/// so it lands in the turn's cost, latency and model columns like any agent's
/// call. <paramref name="Result"/> is null for a call that failed, in which
/// case <paramref name="Cause"/> says why.
/// </summary>
public sealed record ToolkitSubstrateCall(string Label, SubstrateResult? Result, double LatencyMs, string? Cause = null);
