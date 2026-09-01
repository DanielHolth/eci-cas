using System.Diagnostics;
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

/// <summary>Per-agent substrate assignment: which logical class backs it, and whether it calls a substrate at all.</summary>
public sealed class AgentSubstrateEntry
{
    /// <summary>A key into SubstrateOptions.Classes (e.g. "fast-low", "slow-medium").</summary>
    public string Class { get; set; } = "";

    /// <summary>False means this agent never calls the substrate — HandleAsync publishes FallbackResult directly. Defaults true so existing entries are unaffected.</summary>
    public bool UseSubstrate { get; set; } = true;
}

/// <summary>Agent name -> substrate assignment, config-driven so an operator can retarget a role (or turn its LLM use off) without a rebuild. Validated at startup — see AgentSubstrateManifestValidator.</summary>
public sealed class AgentSubstrateManifest
{
    public Dictionary<string, AgentSubstrateEntry> Agents { get; set; } = [];
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

    /// <summary>
    /// `prompt` is the exact text BuildPrompt produced for this call (or, on
    /// a UseSubstrate:false agent, still built for the caller's record even
    /// though no substrate call used it) — Intent forwards it verbatim so
    /// Reflection can later see exactly what it was given, not just what it
    /// said back.
    ///
    /// `degraded` is null on every path that worked, including a
    /// UseSubstrate:false agent — deterministic by configuration is working
    /// as configured, not failing. Non-null means this result is a fallback,
    /// and implementations pass it through SubstrateHealth.Mark so Governance
    /// can see it.
    /// </summary>
    protected abstract void Publish(Envelope envelope, string prompt, TResult result, SubstrateResult? diagnostics, string? degraded);

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var prompt = BuildPrompt(envelope);

        if (!entry.UseSubstrate)
        {
            Publish(envelope, prompt, FallbackResult(envelope), diagnostics: null, degraded: null);
            return;
        }

        // Started before the try so a failure can still report what the
        // attempt cost in wall-clock. Telemetry that only logs on success
        // leaves nothing behind for exactly the turns worth measuring.
        var started = Stopwatch.GetTimestamp();
        try
        {
            var diagnostics = await _substrate.CompleteAsync(entry.Class, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);

            Publish(envelope, prompt, ParseResult(diagnostics), diagnostics, degraded: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} substrate call {Cause} after {LatencyMs}ms, fallback posture {Posture}",
                Name, cause, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Fallback);

            if (Fallback == FallbackPosture.Open)
            {
                Publish(envelope, prompt, FallbackResult(envelope), diagnostics: null, cause);
            }
        }
    }
}
