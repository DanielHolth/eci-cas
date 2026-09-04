using System.Threading.Channels;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Host.TurnLog;

/// <summary>
/// Wildcard subscriber (Topics.All), same shape as ArchiveLogger and
/// SseBroadcaster — an ordinary subscriber, not a display hook baked into
/// any agent. No agent knows this exists.
///
/// It owns the one projection of the bus into readable events, and hands
/// the result to three different consumers: whoever is watching live, a
/// client that just connected and wants the last few events, and the disk
/// sinks. Doing that reduction once, server-side, is what keeps a second
/// surface from having to re-derive it — the drawer in the companion app
/// holds no knowledge of the meta-key table at all.
///
/// A record is handed to the sinks only after SettleMs of quiet, because
/// the reply is not the end of an event: Archivist's write and Reflection's
/// batch land behind it. Settling happens once — anything arriving later
/// still updates the live stream and the in-memory buffer, but does not
/// rewrite a line already on disk.
/// </summary>
public sealed class TurnLogSubscriber : AgentBase
{
    private readonly TurnLogOptions _options;
    private readonly IReadOnlyList<ITurnLogSink> _sinks;
    private readonly CostLedger _ledger;
    private readonly ILogger _logger;

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Queue<Guid> _order = new();
    private readonly Dictionary<Guid, Client> _clients = [];
    private readonly object _gate = new();
    private long _seq;

    private sealed class Entry
    {
        public required TurnRecord Record { get; set; }
        public int Version { get; set; }
        public bool Settled { get; set; }
    }

    private sealed record Client(ChannelWriter<TurnRecord> Writer, string? ProfileId);

    public TurnLogSubscriber(IMessageBus bus, BusActivityTracker activity, ILogger<TurnLogSubscriber> logger,
        IOptions<TurnLogOptions> options, IEnumerable<ITurnLogSink> sinks, CostLedger ledger)
        : base(bus, activity, logger)
    {
        _options = options.Value;
        _sinks = [.. sinks];
        _ledger = ledger;
        _logger = logger;
    }

    public override string Name => "TurnLog";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // Counted here rather than off the record, because a record is
        // rebuilt on every envelope and re-summing it would double-count. A
        // telemetry envelope is published once per call and never replayed,
        // so adding as it lands is the one place the arithmetic is exact.
        if (envelope.Topic == Topics.Telemetry && envelope.Meta.ContainsKey(SubstrateTrace.CostKey))
        {
            _ledger.Add(envelope.Meta.Get<decimal>(SubstrateTrace.CostKey));
        }

        TurnRecord record;
        int version;
        lock (_gate)
        {
            if (!_entries.TryGetValue(envelope.CorrelationId, out var entry))
            {
                entry = new Entry { Record = TurnProjection.Apply(null, envelope, ++_seq) };
                _entries[envelope.CorrelationId] = entry;
                _order.Enqueue(envelope.CorrelationId);
                Evict();
            }
            else
            {
                entry.Record = TurnProjection.Apply(entry.Record, envelope, entry.Record.Seq);
            }

            // Stamped on every envelope, so an event that is still filling in
            // shows the running totals as they stand — and freezes them once
            // it goes quiet, which is what a replayed event should show.
            entry.Record = entry.Record with { SessionCost = _ledger.Session, TotalCost = _ledger.Lifetime };

            entry.Version++;
            record = entry.Record;
            version = entry.Version;

            foreach (var client in _clients.Values.Where(c => Wants(c, record)))
            {
                client.Writer.TryWrite(record);
            }
        }

        // Fire and forget on purpose: a subscriber that awaited its own
        // settle timer would hold the bus queue for three seconds per event.
        _ = SettleAsync(envelope.CorrelationId, version, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Events a client that just connected has missed, oldest first.</summary>
    public IReadOnlyList<TurnRecord> Recent(string? profileId)
    {
        lock (_gate)
        {
            return [.. _order
                .Select(id => _entries[id].Record)
                .Where(r => profileId is null || r.ProfileId is null || r.ProfileId == profileId)];
        }
    }

    public ChannelReader<TurnRecord> Connect(string? profileId, out Guid clientId)
    {
        var channel = Channel.CreateUnbounded<TurnRecord>();
        var id = Guid.NewGuid();
        lock (_gate)
        {
            _clients[id] = new Client(channel.Writer, profileId);
        }

        clientId = id;
        return channel.Reader;
    }

    public void Disconnect(Guid clientId)
    {
        lock (_gate)
        {
            if (_clients.Remove(clientId, out var client))
            {
                client.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// One delay per envelope, and only the last one still matches the
    /// version it captured — which is what makes "quiet for SettleMs" true
    /// without a timer to own, cancel and dispose per event.
    /// </summary>
    private async Task SettleAsync(Guid correlationId, int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.SettleMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TurnRecord record;
        lock (_gate)
        {
            if (!_entries.TryGetValue(correlationId, out var entry) || entry.Settled || entry.Version != version)
            {
                return;
            }

            entry.Settled = true;
            record = entry.Record;
        }

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Agent} sink {Sink} failed for event {Seq}", Name, sink.GetType().Name, record.Seq);
            }
        }

        // Once per settled event rather than once per call: the lifetime
        // total only has to survive a restart, and a file rewritten on every
        // substrate trace would write five times per turn to say the same
        // thing.
        await _ledger.PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A client that named no profile sees everything; one that did also sees the events nobody owns, since the persona's own thinking belongs to every window rather than to none.</summary>
    private static bool Wants(Client client, TurnRecord record) =>
        client.ProfileId is null || record.ProfileId is null || record.ProfileId == client.ProfileId;

    private void Evict()
    {
        while (_order.Count > Math.Max(1, _options.Retain))
        {
            _entries.Remove(_order.Dequeue());
        }
    }
}
