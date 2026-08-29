using EciCas.Agents.Reasoning;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Recall;

/// <summary>
/// Thin bus adapter over IArchiveStore: applies tier policy (MaxPerPath),
/// runs the store's internal N-way lookup, and folds the result into
/// exactly one advisory. Deterministic tier — no substrate call. See plan
/// §3.3/§3.4: the store does the parallel work, this agent only decides how
/// much of it to ask for.
/// </summary>
public sealed class RecallAgent : AgentBase
{
    public const string ResultsKey = "recall.results";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly RecallOptions _options;

    public RecallAgent(IMessageBus bus, BusActivityTracker activity, ILogger<RecallAgent> logger, IArchiveStore store, IOptions<RecallOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _options = options.Value;
    }

    public override string Name => "Recall";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.LookupPaths];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var paths = envelope.Meta.Get<string[]>(ReasoningAgent.LookupPathsKey) ?? [];
        var records = await _store.LookupAsync(paths, _options.MaxPerPath, cancellationToken).ConfigureAwait(false);

        var text = records.Count == 0
            ? "nothing on file"
            : string.Join("; ", records.Select(r => r.Content));

        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(ResultsKey, text));
        _bus.Publish(Topics.Advisories, advisory);
    }
}
