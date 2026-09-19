using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
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
/// A run is asynchronous to the turn that asked for it: turn N cannot wait
/// on it, and Governance tears turn N's bundle down long before a slow script
/// finishes. So a finished run comes back as a turn of its own -- a
/// perception stamped <see cref="ToolkitTrigger"/> carrying the findings --
/// which Intent answers like any other input. Same shape as Reflection's
/// pushed ideas, and guarded the same way: the perception carries generation
/// 1, and a toolkit-triggered turn is never itself routed to a toolkit.
/// </summary>
public sealed class ToolkitManagerAgent : AgentBase
{
    /// <summary>Value of <see cref="ReflectionAgent.TriggeredByKey"/> on a perception that reports a finished toolkit run.</summary>
    public const string ToolkitTrigger = "toolkit";

    /// <summary>The findings block is longer than a person's input, so it is capped on its own terms rather than at the input-length knob.</summary>
    private const int FindingsChars = 1200;

    private readonly IMessageBus _bus;
    private readonly IToolkitCatalog _catalog;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ToolkitOptions _options;
    private readonly ILogger<ToolkitManagerAgent> _logger;

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

        // A run's own report is not a request. Routing it would let a
        // search's findings ("...latest news...") ask for another search.
        if (envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) == ToolkitTrigger)
        {
            return;
        }

        await TryRouteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private void OnResult(Envelope envelope)
    {
        var name = envelope.Meta.Get<string>(ToolkitResult.NameKey) ?? "toolkit";
        var command = envelope.Meta.Get<string>(ToolkitResult.CommandKey) ?? string.Empty;
        var output = envelope.Meta.Get<string>(ToolkitResult.OutputKey) ?? string.Empty;
        var success = envelope.Meta.Get<bool>(ToolkitResult.SuccessKey);
        var error = envelope.Meta.Get<string>(ToolkitResult.ErrorKey);

        var findings = success
            ? string.IsNullOrWhiteSpace(output) ? "it completed with nothing to report." : $"it reported: {output}"
            : $"it failed: {(string.IsNullOrWhiteSpace(error) ? output : error)}";

        // Written as an instruction to Intent because Intent reads this as its
        // input, and nothing else tells it a toolkit ran. The person's own
        // words are quoted so the answer can be about what they asked.
        var text = PromptCap.Apply(
            $"I ran my {name} toolkit for the request \"{PromptCap.Apply(command, 200)}\" and {findings} " +
            "Answer that request now from this, briefly and in my own voice" +
            (success ? "." : ", and say plainly that it did not work."),
            FindingsChars);

        var report = Envelope.Create(Topics.Perception, Name, Severity.Neutral,
            MetaBag.Empty
                .With(PerceptionAgent.TextKey, text)
                .With(ReflectionAgent.TriggeredByKey, ToolkitTrigger)
                .With(ToolkitResult.NameKey, name)
                .With(ToolkitResult.SuccessKey, success)
                .With(ToolkitResult.ReferencesKey, envelope.Meta.Get<IReadOnlyList<ToolkitReference>>(ToolkitResult.ReferencesKey) ?? []),
            generation: 1);
        _bus.Publish(Topics.Perception, report);
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

}
