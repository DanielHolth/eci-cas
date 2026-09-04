using EciCas.Core;

namespace EciCas.Bus;

/// <summary>
/// What one substrate call cost, published as its own envelope.
///
/// A call is its own event because the envelope an agent publishes does not
/// map one-to-one onto the calls it made: Recall fans out one call per pair
/// behind a single advisory, Reflection's call spans a whole batch of turns,
/// and Archivist publishes only on flush. Stamping diagnostics onto an
/// agent's own envelope would therefore lose calls, so they get their own
/// topic and are tied back to the turn by CorrelationId — the same grouping
/// Governance bundles on.
///
/// Nothing in the process subscribes to this. It exists for the display
/// layer and the disk log; publishing it costs one fan-out over subscribers
/// that already take Topics.All.
/// </summary>
public static class SubstrateTrace
{
    public const string AgentKey = "substrate.agent";
    public const string ClassKey = "substrate.class";

    /// <summary>Which call this was, for agents that make more than one kind — e.g. Recall's per-pair picking call names the pair.</summary>
    public const string LabelKey = "substrate.label";

    public const string LatencyKey = "substrate.latency_ms";
    public const string TokensKey = "substrate.tokens";
    public const string CostKey = "substrate.cost";

    /// <summary>Publishes what a completed call cost. `label` is null for agents that only ever make one kind of call.</summary>
    public static void Publish(IMessageBus bus, Envelope trigger, string agent, string substrateClass, SubstrateResult result, string? label = null) =>
        Publish(bus, trigger, agent, substrateClass, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost, label, degraded: null);

    /// <summary>
    /// Publishes what a failed call cost, which is the wall-clock it burned
    /// before it gave up. Telemetry that only reports successes leaves
    /// nothing behind for exactly the turns worth measuring.
    /// </summary>
    public static void PublishFailure(IMessageBus bus, Envelope trigger, string agent, string substrateClass, double latencyMs, string cause, string? label = null) =>
        Publish(bus, trigger, agent, substrateClass, latencyMs, tokens: null, cost: null, label, cause);

    private static void Publish(IMessageBus bus, Envelope trigger, string agent, string substrateClass, double latencyMs, int? tokens, decimal? cost, string? label, string? degraded)
    {
        var meta = MetaBag.Empty
            .With(AgentKey, agent)
            .With(ClassKey, substrateClass)
            .With(LatencyKey, latencyMs);

        if (label is { Length: > 0 })
        {
            meta = meta.With(LabelKey, label);
        }

        if (tokens is { } t)
        {
            meta = meta.With(TokensKey, t);
        }

        if (cost is { } c)
        {
            meta = meta.With(CostKey, c);
        }

        bus.Publish(Topics.Telemetry, trigger.Derive(Topics.Telemetry, agent, trigger.Severity, SubstrateHealth.Mark(meta, degraded)));
    }
}
