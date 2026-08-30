using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

/// <summary>Marker for agents whose substrate class comes from AgentSubstrateManifest, so Host can validate the manifest's coverage without reflecting over open generics.</summary>
public interface ICognitiveAgent
{
}

/// <summary>Agent name -> logical substrate class (e.g. "fast-low", "slow-medium"), config-driven so an operator can retarget a role without a rebuild. Validated at startup — see AgentSubstrateManifestValidator.</summary>
public sealed class AgentSubstrateManifest
{
    public Dictionary<string, string> Agents { get; set; } = [];
}

/// <summary>
/// An agent whose result comes from a substrate call rather than fixed logic:
/// builds a prompt, calls ISubstrateProvider, and publishes the parsed result
/// (or a fallback, per Fallback) with latency/token/cost diagnostics logged.
/// </summary>
public abstract class CognitiveAgent<TResult> : AgentBase, ICognitiveAgent
{
    private readonly ISubstrateProvider _substrate;
    private readonly ILogger _logger;
    private readonly AgentSubstrateManifest _agentSubstrates;

    protected CognitiveAgent(IMessageBus bus, BusActivityTracker activity, ILogger logger, ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates)
        : base(bus, activity, logger)
    {
        _substrate = substrate;
        _logger = logger;
        _agentSubstrates = agentSubstrates.Value;
    }

    protected abstract FallbackPosture Fallback { get; }

    protected abstract string BuildPrompt(Envelope envelope);

    protected abstract TResult ParseResult(SubstrateResult result);

    /// <summary>Only called when Fallback is Open.</summary>
    protected abstract TResult FallbackResult(Envelope envelope);

    protected abstract void Publish(Envelope envelope, TResult result, SubstrateResult? diagnostics);

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var substrateClass))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var prompt = BuildPrompt(envelope);

        try
        {
            var diagnostics = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
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
