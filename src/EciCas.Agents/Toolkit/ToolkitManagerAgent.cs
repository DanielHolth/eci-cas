using System.Collections.Concurrent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// A fan-out participant, not an Intent-initiated tool call: this agent sees
/// every turn on events.perception like Impulse/Identity/Sight, decides for
/// itself whether a toolkit applies, and stays silent the rest of the time.
/// Intent is simply the spokesperson -- it never initiates a toolkit or
/// parses toolkit syntax out of its own output.
///
/// Routing is semantic, not deterministic: a real ask ("clean up my
/// downloads folder to make disk space") will rarely use a toolkit's own
/// vocabulary, so the turn is embedded and matched by cosine similarity
/// against each <see cref="IToolkitCatalog"/> entry's prose trigger
/// exemplars -- the same shape as ImpulseAgent's EmergencyReflex. This is
/// deliberately not a substrate/LLM call: it has to close within the turn
/// every time, and a toolkit that needs real reasoning to run (PowerShell's
/// NL-to-script step) does that heavy lifting itself, inside the toolkit,
/// once routing has already picked it.
///
/// Deliberately never added to GovernanceOptions.BundleRoster: this agent
/// is silent on the large majority of turns by design, and roster
/// membership is what Governance uses to track a faculty as impaired when
/// it doesn't answer. Silence here is the normal case, not a failure.
///
/// Handles the async mismatch a multi-second script creates: a run started
/// on turn N cannot report into turn N's bundle (Governance tears that
/// bundle down once concluded, long before a slow script finishes). So a
/// finished run is tracked in <see cref="_runs"/>, keyed by the
/// ToolkitRequest's own CorrelationId, and reported as an advisory derived
/// from whatever events.perception envelope arrives next -- the next live
/// turn, which is guaranteed to have a fresh, real bundle waiting.
/// </summary>
public sealed class ToolkitManagerAgent : AgentBase
{
    /// <summary>What ToolkitManager has to say about a finished run, when it has something to say.</summary>
    public const string AdviceKey = "toolkit.advice";

    private readonly IMessageBus _bus;
    private readonly IToolkitCatalog _catalog;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ToolkitOptions _options;
    private readonly ILogger<ToolkitManagerAgent> _logger;

    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private readonly SemaphoreSlim _exemplarLock = new(1, 1);
    private (string Name, float[][] Vectors)[]? _exemplars;

    public ToolkitManagerAgent(
        IMessageBus bus,
        IToolkitCatalog catalog,
        IEmbeddingProvider embeddings,
        IOptions<ToolkitOptions> options,
        BusActivityTracker activity,
        ILogger<ToolkitManagerAgent> logger)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _catalog = catalog;
        _embeddings = embeddings;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "ToolkitManager";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.ToolkitResult];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (envelope.Topic == Topics.ToolkitResult)
        {
            OnResult(envelope);
            return;
        }

        ReportFinishedRuns(envelope);
        await TryRouteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private void OnResult(Envelope envelope)
    {
        // Recorded, never published from here -- the run is only reported
        // once a live turn arrives to carry it. See ReportFinishedRuns.
        _runs[envelope.CorrelationId] = new RunState(
            envelope.Meta.Get<string>(ToolkitResult.NameKey) ?? "toolkit",
            envelope.Meta.Get<string>(ToolkitResult.OutputKey) ?? string.Empty,
            envelope.Meta.Get<bool>(ToolkitResult.SuccessKey),
            envelope.Meta.Get<string>(ToolkitResult.ErrorKey),
            Finished: true,
            envelope.Meta.Get<IReadOnlyList<ToolkitReference>>(ToolkitResult.ReferencesKey));
    }

    private void ReportFinishedRuns(Envelope perception)
    {
        foreach (var (correlationId, run) in _runs)
        {
            if (!run.Finished)
            {
                continue;
            }

            if (!_runs.TryRemove(correlationId, out _))
            {
                continue;
            }

            var text = string.IsNullOrWhiteSpace(run.Output)
                ? run.Success ? $"{run.Name} completed successfully." : $"{run.Name} failed. {run.Error ?? "No output returned."}"
                : run.Success ? $"{run.Name} reported: {run.Output}" : $"{run.Name} failed: {run.Error ?? run.Output}";

            var advisory = perception.Derive(Topics.Advisories, Name, Severity.Neutral,
                MetaBag.Empty
                    .With(AdviceKey, text)
                    .With(ToolkitResult.NameKey, run.Name)
                    .With(ToolkitResult.OutputKey, run.Output)
                    .With(ToolkitResult.SuccessKey, run.Success)
                    .With(ToolkitResult.ReferencesKey, run.References ?? []));

            _bus.Publish(Topics.Advisories, advisory);
        }
    }

    private async Task TryRouteAsync(Envelope perception, CancellationToken cancellationToken)
    {
        try
        {
            var text = perception.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || !_embeddings.Available)
            {
                return;
            }

            var exemplars = await EnsureExemplarsAsync(cancellationToken).ConfigureAwait(false);
            if (exemplars.Length == 0)
            {
                return;
            }

            var asked = await _embeddings.EmbedAsync([text], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            if (asked.Count == 0)
            {
                return;
            }

            string? bestName = null;
            var bestScore = 0.0;
            foreach (var (name, vectors) in exemplars)
            {
                var score = vectors.Max(v => VectorMath.Cosine(asked[0], v));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestName = name;
                }
            }

            _logger.LogDebug("ToolkitManager route: best {Best} score {Score:F3}, floor {Floor:F3}", bestName, bestScore, _options.RouteFloor);

            if (bestName is null || bestScore < _options.RouteFloor)
            {
                return;
            }

            var request = Envelope.Create(Topics.ToolkitRequest, Name, Severity.Neutral, ToolkitRequest.Build(bestName, text));
            _runs[request.CorrelationId] = new RunState(bestName, string.Empty, false, null, Finished: false);
            _bus.Publish(Topics.ToolkitRequest, request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Routing runs unattended on every turn; a bad match should
            // never take this agent off the bus for the rest of the session.
            _logger.LogWarning(ex, "ToolkitManager routing failed");
        }
    }

    private async Task<(string Name, float[][] Vectors)[]> EnsureExemplarsAsync(CancellationToken cancellationToken)
    {
        if (_exemplars is not null)
        {
            return _exemplars;
        }

        await _exemplarLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_exemplars is not null)
            {
                return _exemplars;
            }

            var built = new List<(string, float[][])>();
            foreach (var descriptor in _catalog.All)
            {
                if (descriptor.Triggers.Count == 0)
                {
                    continue;
                }

                var vectors = await _embeddings.EmbedAsync(descriptor.Triggers, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
                built.Add((descriptor.Name, [.. vectors]));
            }

            _exemplars = [.. built];
            return _exemplars;
        }
        finally
        {
            _exemplarLock.Release();
        }
    }

    private sealed record RunState(string Name, string Output, bool Success, string? Error, bool Finished, IReadOnlyList<ToolkitReference>? References = null);
}
