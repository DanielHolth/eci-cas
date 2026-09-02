using System.Text.Json;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Host;

/// <summary>
/// Wildcard subscriber (Topics.All) that appends every envelope to a JSONL
/// file. The complete audit trail, bought with zero coupling and zero relay
/// hops — no agent knows this exists. Storage grows Parquet later; this stays
/// the append-only log either way.
/// </summary>
public sealed class ArchiveLogger : AgentBase
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ArchiveLogger(IMessageBus bus, BusActivityTracker activity, ILogger<ArchiveLogger> logger, string path = "archive.jsonl")
        : base(bus, activity, logger) => _path = path;

    public override string Name => "ArchiveLogger";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(new
        {
            envelope.EventId,
            envelope.CorrelationId,
            envelope.Topic,
            envelope.PublishedBy,
            envelope.Timestamp,
            envelope.Severity,
            envelope.Generation,
        });

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
