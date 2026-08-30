using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Consolidator;

/// <summary>
/// Parallel publisher on events.bundle alongside Intent — never through the
/// live reply path (see plan's opening rationale: this is exactly the hop
/// that broke the Python bus). Batches bundle content into ArchiveRecords and
/// flushes to the store every BatchSize bundles, then announces the epoch on
/// system.control so Self can invalidate its persona cache.
/// </summary>
public sealed class ConsolidatorAgent : AgentBase
{
    public const string ControlKindKey = "control.kind";
    public const string EpochIdKey = "control.epoch_id";
    public const string WrittenKind = "Written";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly ConsolidatorOptions _options;
    private readonly List<ArchiveRecord> _pending = [];
    private readonly object _pendingLock = new();

    public ConsolidatorAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ConsolidatorAgent> logger, IArchiveStore store, IOptions<ConsolidatorOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _options = options.Value;
    }

    public override string Name => "Consolidator";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        // Write under the same significant-word paths Reasoning proposes when
        // querying (see SignificantWords) so a later lookup actually
        // intersects what got stored here — plus a fixed "turn" anchor so
        // short/low-signal turns are still recoverable by category.
        var paths = SignificantWords.Extract(text).Append("turn").Distinct();
        var newRecords = paths.Select(path => new ArchiveRecord(path, text, envelope.Timestamp)).ToList();

        List<ArchiveRecord>? batch = null;
        lock (_pendingLock)
        {
            _pending.AddRange(newRecords);
            if (_pending.Count >= _options.BatchSize)
            {
                batch = [.. _pending];
                _pending.Clear();
            }
        }

        if (batch is null)
        {
            return;
        }

        await _store.WriteAsync(batch, cancellationToken).ConfigureAwait(false);

        var epochId = Guid.NewGuid();
        var written = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ControlKindKey, WrittenKind).With(EpochIdKey, epochId));
        _bus.Publish(Topics.SystemControl, written);
    }
}
