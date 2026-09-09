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

/// <summary>Marker for agents backed by a Substrates:Agents entry, so Host can validate that table's coverage without reflecting over open generics.</summary>
public interface ICognitiveAgent
{
}

/// <summary>
/// An agent whose result comes from a substrate call rather than fixed logic:
/// builds a prompt, calls ISubstrateProvider, and publishes the parsed result
/// (or a fallback, per Fallback) with latency/token/cost diagnostics logged.
/// </summary>
public abstract class CognitiveAgent<TResult> : AgentBase, ICognitiveAgent
{
    private readonly ISubstrateProvider _substrate;
    private readonly IMessageBus _bus;
    private readonly ILogger _logger;
    private readonly SubstrateOptions _substrates;

    protected CognitiveAgent(IMessageBus bus, BusActivityTracker activity, ILogger logger, ISubstrateProvider substrate, IOptions<SubstrateOptions> substrates)
        : base(bus, activity, logger)
    {
        _substrate = substrate;
        _bus = bus;
        _logger = logger;
        _substrates = substrates.Value;
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
        if (!_substrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No substrate entry for agent '{Name}' — add one to appsettings.json's Substrates:Agents section.");
        }

        var prompt = BuildPrompt(envelope);

        // Debug only, and the whole prompt: tuning an instruction file means
        // seeing exactly what the agent was handed, recalled facts and woken
        // notes included, not a summary of it.
        _logger.LogDebug("{Agent} prompt >>>\n{Prompt}", Name, prompt);

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
            var diagnostics = await _substrate.CompleteAsync(Name, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);
            _logger.LogDebug("{Agent} response <<<\n{Response}", Name, diagnostics.Text);
            SubstrateTrace.Publish(_bus, envelope, Name, diagnostics);

            Publish(envelope, prompt, ParseResult(diagnostics), diagnostics, degraded: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = SubstrateHealth.Classify(ex);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.LogWarning("{Agent} substrate call {Cause} after {LatencyMs}ms, fallback posture {Posture}",
                Name, cause, elapsed, Fallback);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, elapsed, cause);

            if (Fallback == FallbackPosture.Open)
            {
                Publish(envelope, prompt, FallbackResult(envelope), diagnostics: null, cause);
            }
        }
    }
}
