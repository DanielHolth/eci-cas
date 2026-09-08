using System.Collections.Concurrent;
using System.Globalization;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Serialization;

namespace EciCas.Host.Telemetry;

/// <summary>
/// Every SubstrateTrace call, one row on disk with what it cost and how long
/// it took. Its own subscriber rather than folded into TurnLogSubscriber: a
/// turn record is a display projection settled behind a quiet timer, and
/// this is neither — it is the raw call stream, kept for later analysis
/// (which agent is slow, which class is expensive) rather than for showing
/// a person what happened.
///
/// Entirely decoupled from the UI: nothing here reads a TurnRecord and
/// nothing the surface does can affect it. Off unless TelemetryLog:Directory
/// is set, matching how the JSONL turn log opts in.
///
/// One file per UTC day (telemetry-YYYY-MM-DD.parquet), buffered in memory
/// and flushed on a row count or a timer, whichever comes first — the same
/// read-merge-rewrite ParquetArchiveStore uses, since Parquet has no
/// in-place append. A crashed host loses at most one flush interval of
/// telemetry, which is the right trade for a log that exists to explain
/// cost after the fact, not to gate anything live.
/// </summary>
public sealed class TelemetryLogAgent : AgentBase
{
    private sealed class Row
    {
        public string Timestamp { get; set; } = "";
        public Guid CorrelationId { get; set; }
        public string Agent { get; set; } = "";
        public string Class { get; set; } = "";
        public string? Label { get; set; }
        public double LatencyMs { get; set; }
        public int? Tokens { get; set; }
        public decimal? Cost { get; set; }
        public string? Degraded { get; set; }
    }

    private readonly string? _directory;
    private readonly int _flushEvery;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<Row> _pending = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _pendingCount;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;

    public TelemetryLogAgent(IMessageBus bus, BusActivityTracker activity, ILogger<TelemetryLogAgent> logger,
        IOptions<TelemetryLogOptions> options)
        : base(bus, activity, logger)
    {
        _logger = logger;
        var opts = options.Value;
        _directory = string.IsNullOrWhiteSpace(opts.Directory) ? null : opts.Directory;
        _flushEvery = Math.Max(1, opts.FlushEvery);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Max(100, opts.FlushMs));
        if (_directory is not null)
        {
            Directory.CreateDirectory(_directory);
        }
    }

    public override string Name => "TelemetryLog";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Telemetry];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_directory is null || !envelope.Meta.ContainsKey(SubstrateTrace.LatencyKey))
        {
            return;
        }

        _pending.Enqueue(new Row
        {
            Timestamp = envelope.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = envelope.CorrelationId,
            Agent = envelope.Meta.Get<string>(SubstrateTrace.AgentKey) ?? envelope.PublishedBy,
            Class = envelope.Meta.Get<string>(SubstrateTrace.ClassKey) ?? "",
            Label = envelope.Meta.Get<string>(SubstrateTrace.LabelKey),
            LatencyMs = envelope.Meta.Get<double>(SubstrateTrace.LatencyKey),
            Tokens = envelope.Meta.ContainsKey(SubstrateTrace.TokensKey) ? envelope.Meta.Get<int>(SubstrateTrace.TokensKey) : null,
            Cost = envelope.Meta.ContainsKey(SubstrateTrace.CostKey) ? envelope.Meta.Get<decimal>(SubstrateTrace.CostKey) : null,
            Degraded = envelope.Meta.Get<string>(SubstrateHealth.DegradedKey),
        });

        var count = Interlocked.Increment(ref _pendingCount);
        if (count >= _flushEvery || DateTimeOffset.UtcNow - _lastFlush >= _flushInterval)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending.IsEmpty)
            {
                return;
            }

            var byDay = new Dictionary<string, List<Row>>();
            while (_pending.TryDequeue(out var row))
            {
                Interlocked.Decrement(ref _pendingCount);
                var day = row.Timestamp[..10];
                if (!byDay.TryGetValue(day, out var list))
                {
                    byDay[day] = list = [];
                }

                list.Add(row);
            }

            foreach (var (day, rows) in byDay)
            {
                await AppendAsync(day, rows, cancellationToken).ConfigureAwait(false);
            }

            _lastFlush = DateTimeOffset.UtcNow;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task AppendAsync(string day, List<Row> rows, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory!, $"telemetry-{day}.parquet");
        try
        {
            var existing = File.Exists(path)
                ? (await ParquetSerializer.DeserializeAsync<Row>(path, cancellationToken: cancellationToken).ConfigureAwait(false)).Data
                : [];

            var combined = new List<Row>(existing.Count + rows.Count);
            combined.AddRange(existing);
            combined.AddRange(rows);

            var temp = path + ".tmp";
            await ParquetSerializer.SerializeAsync(combined, temp, cancellationToken: cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same rule as every other disk sink here: a log that cannot be
            // written is not a call that failed, so the rows are dropped
            // rather than retried into an unbounded queue.
            _logger.LogWarning(ex, "{Agent} could not write {Path}", Name, path);
        }
    }
}
