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
/// It is on the BundleRoster and publishes an advisory on every turn, empty
/// unless a toolkit was routed. That is what lets Intent hear about a run
/// before it answers: when one started, Intent acknowledges it in a line
/// instead of guessing at the answer the toolkit is about to fetch.
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

    /// <summary>
    /// The findings block is longer than a person's input, so it is capped on
    /// its own terms rather than at the input-length knob. A ceiling, not a
    /// target: a toolkit shortens its own output. It exists because this text
    /// is folded into every advisor's prompt on the report turn; the report
    /// is a dead end (never routed, never archived), so unlike the 240-char
    /// default it cannot compound across hops.
    /// </summary>
    private const int FindingsChars = 5000;

    /// <summary>Meta key on this agent's advisory: what Intent should say while a routed toolkit is still running.</summary>
    public const string AdviceKey = "toolkit.advice";

    private const string RunningAdvice =
        "A toolkit has just started on their request and will report back on its own. Do not answer the request yourself. Say only one short sentence that you are looking into it now.";

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
        if (envelope.Topic == Topics.ToolkitResult)
        {
            if (_options.Enabled)
            {
                OnResult(envelope);
            }

            return;
        }

        // A run's own report is not a request. Routing it would let a
        // search's findings ("...latest news...") ask for another search.
        var routed = _options.Enabled
            && envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) != ToolkitTrigger
            && await TryRouteAsync(envelope, cancellationToken).ConfigureAwait(false);

        // Routed: a run has started. Otherwise, on an ordinary turn, Intent is
        // told what it can do, because nothing else does -- without this it
        // answers "I can't search the web" while the search toolkit sits idle.
        // Built from the catalog, so a new toolkit announces itself.
        var advice = routed ? RunningAdvice
            : _options.Enabled && envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) != ToolkitTrigger ? CapabilityAdvice()
            : null;
        var meta = advice is null ? MetaBag.Empty : MetaBag.Empty.With(AdviceKey, advice);
        _bus.Publish(Topics.Advisories, envelope.Derive(Topics.Advisories, Name, envelope.Severity, meta));
    }

    private string? CapabilityAdvice()
    {
        if (_catalog.All.Count == 0)
        {
            return null;
        }

        var abilities = string.Join("; ", _catalog.All.Select(t => $"{t.Name} ({t.Description.Split(" -- ")[0].TrimEnd('.')})"));
        return $"You have toolkits: {abilities}. One starts by itself when the person clearly asks for it, and its result comes back to you. " +
            "Never say you cannot do these things. If they ask for one and nothing has started, ask what exactly they want looked up or done.";
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
            "Treat this as current and more recent than anything I remember. Answer that request now from this in one or two short sentences, in my own voice" +
            (success ? "." : ", and say plainly that it did not work."),
            FindingsChars);

        var report = Envelope.Create(Topics.Perception, Name, Severity.Neutral,
            MetaBag.Empty
                .With(PerceptionAgent.TextKey, text)
                .With(ReflectionAgent.TriggeredByKey, ToolkitTrigger)
                .With(ToolkitResult.NameKey, name)
                .With(ToolkitResult.CommandKey, command)
                .With(ToolkitResult.SuccessKey, success)
                .With(ToolkitResult.ReferencesKey, envelope.Meta.Get<IReadOnlyList<ToolkitReference>>(ToolkitResult.ReferencesKey) ?? []),
            generation: 1);
        _bus.Publish(Topics.Perception, report);
    }

    private async Task<bool> TryRouteAsync(Envelope perception, CancellationToken cancellationToken)
    {
        try
        {
            var text = perception.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || !_embeddings.Available)
            {
                return false;
            }

            var exemplars = await EnsureExemplarsAsync(cancellationToken).ConfigureAwait(false);
            if (exemplars.Length == 0)
            {
                return false;
            }

            var asked = await _embeddings.EmbedAsync([text], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            if (asked.Count == 0)
            {
                return false;
            }

            string? bestName = null;
            var bestScore = 0.0;
            var runnerUp = 0.0;
            foreach (var (name, vectors) in exemplars)
            {
                var score = vectors.Max(v => VectorMath.Cosine(asked[0], v));
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    bestName = name;
                }
                else if (score > runnerUp)
                {
                    runnerUp = score;
                }
            }

            var margin = bestScore - runnerUp;
            var routed = bestName is not null && bestScore >= _options.RouteFloor && margin >= _options.RouteMargin;
            _logger.LogInformation(
                "ToolkitManager route: best {Best} score {Score:F3} margin {Margin:F3} (floor {Floor:F3}, margin {MinMargin:F3}) -> {Outcome}",
                bestName, bestScore, margin, _options.RouteFloor, _options.RouteMargin, routed ? "run" : "no toolkit");

            if (!routed || bestName is null)
            {
                return false;
            }

            var request = Envelope.Create(Topics.ToolkitRequest, Name, Severity.Neutral, ToolkitRequest.Build(bestName, text));
            _bus.Publish(Topics.ToolkitRequest, request);
            return true;
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
            return false;
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
