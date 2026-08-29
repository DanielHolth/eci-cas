using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Bus;

/// <summary>
/// What an agent does when its substrate call fails or is unavailable.
/// Open: publish a degraded-but-real result so downstream agents proceed.
/// Closed: publish nothing and let Governance's bundle timeout cover the gap.
/// </summary>
public enum FallbackPosture
{
    Open,
    Closed,
}

/// <summary>
/// An agent whose result comes from a substrate call rather than fixed logic:
/// builds a prompt, calls ISubstrateProvider, and publishes the parsed result
/// (or a fallback, per Fallback) with latency/token/cost diagnostics logged.
/// </summary>
public abstract class CognitiveAgent<TResult> : AgentBase
{
    private readonly ISubstrateProvider _substrate;
    private readonly ILogger _logger;

    protected CognitiveAgent(IMessageBus bus, BusActivityTracker activity, ILogger logger, ISubstrateProvider substrate)
        : base(bus, activity, logger)
    {
        _substrate = substrate;
        _logger = logger;
    }

    /// <summary>Logical substrate class (e.g. "fast-low", "slow-medium") — resolved to a concrete provider by the substrate registry, not by this agent.</summary>
    protected abstract string SubstrateClass { get; }

    protected abstract FallbackPosture Fallback { get; }

    protected abstract string BuildPrompt(Envelope envelope);

    protected abstract TResult ParseResult(SubstrateResult result);

    /// <summary>Only called when Fallback is Open.</summary>
    protected abstract TResult FallbackResult(Envelope envelope);

    protected abstract void Publish(Envelope envelope, TResult result, SubstrateResult? diagnostics);

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(envelope);

        try
        {
            var diagnostics = await _substrate.CompleteAsync(SubstrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, {Cost} cost",
                Name, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);

            Publish(envelope, ParseResult(diagnostics), diagnostics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} substrate call failed, fallback posture {Posture}", Name, Fallback);

            if (Fallback == FallbackPosture.Open)
            {
                Publish(envelope, FallbackResult(envelope), diagnostics: null);
            }
        }
    }
}
