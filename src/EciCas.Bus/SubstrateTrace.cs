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

    /// <summary>Which call this was, for agents that make more than one kind — e.g. Recall's per-pair picking call names the pair.</summary>
    public const string LabelKey = "substrate.label";

    public const string LatencyKey = "substrate.latency_ms";
    public const string TokensKey = "substrate.tokens";

    /// <summary>Which endpoint served the call, and the model id it sent.
    /// A tier can mix local and vendor agents freely, so "is this turn
    /// running on my own hardware" is only answerable per call.</summary>
    public const string ProviderKey = "substrate.provider";
    public const string ModelKey = "substrate.model";

    /// <summary>The split behind <see cref="TokensKey"/>, when the provider reports one. Input-heavy by design, and priced differently per direction.</summary>
    public const string PromptTokensKey = "substrate.tokens.prompt";
    public const string CompletionTokensKey = "substrate.tokens.completion";
    public const string CostKey = "substrate.cost";

    /// <summary>Publishes what a completed call cost. `label` is null for agents that only ever make one kind of call.</summary>
    public static void Publish(IMessageBus bus, Envelope trigger, string agent, SubstrateResult result, string? label = null) =>
        Publish(bus, trigger, agent, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost, label, degraded: null,
            result.Provider, result.Model, result.PromptTokens, result.CompletionTokens);

    /// <summary>
    /// Publishes what a failed call cost, which is the wall-clock it burned
    /// before it gave up. Telemetry that only reports successes leaves
    /// nothing behind for exactly the turns worth measuring.
    /// </summary>
    public static void PublishFailure(IMessageBus bus, Envelope trigger, string agent, double latencyMs, string cause, string? label = null) =>
        Publish(bus, trigger, agent, latencyMs, tokens: null, cost: null, label, cause);

    private static void Publish(IMessageBus bus, Envelope trigger, string agent, double latencyMs, int? tokens, decimal? cost, string? label, string? degraded) =>
        Publish(bus, trigger, agent, latencyMs, tokens, cost, label, degraded, provider: null, model: null, promptTokens: null, completionTokens: null);

    private static void Publish(IMessageBus bus, Envelope trigger, string agent, double latencyMs, int? tokens, decimal? cost,
        string? label, string? degraded, string? provider, string? model, int? promptTokens, int? completionTokens)
    {
        var meta = MetaBag.Empty
            .With(AgentKey, agent)
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

        if (provider is { Length: > 0 })
        {
            meta = meta.With(ProviderKey, provider);
        }

        if (model is { Length: > 0 })
        {
            meta = meta.With(ModelKey, model);
        }

        if (promptTokens is { } pt)
        {
            meta = meta.With(PromptTokensKey, pt);
        }

        if (completionTokens is { } ct)
        {
            meta = meta.With(CompletionTokensKey, ct);
        }

        bus.Publish(Topics.Telemetry, trigger.Derive(Topics.Telemetry, agent, trigger.Severity, SubstrateHealth.Mark(meta, degraded)));
    }
}
