using System.Collections.Concurrent;
using System.Threading.Channels;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Host;

/// <summary>
/// Wildcard subscriber (Topics.All), same shape as ArchiveLogger and
/// ConsoleSubscriber — an ordinary subscriber, not a display hook baked into
/// any agent. Fans every envelope out to whichever SSE clients are currently
/// connected via their own unbounded per-client channel; a slow or gone
/// client only backs up its own channel, never this agent's consumer loop.
///
/// Every client sees every envelope. A client used to be able to name a
/// profile and see only that profile's turns, with the owner of a turn
/// learned from its perception and every later envelope matched back by
/// CorrelationId. An account has one profile, so that map had one owner.
/// </summary>
public sealed class SseBroadcaster : AgentBase
{
    private readonly ConcurrentDictionary<Guid, ChannelWriter<Envelope>> _clients = new();

    public SseBroadcaster(IMessageBus bus, BusActivityTracker activity, ILogger<SseBroadcaster> logger)
        : base(bus, activity, logger)
    {
    }

    public override string Name => "SseBroadcaster";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        foreach (var writer in _clients.Values)
        {
            writer.TryWrite(envelope);
        }

        return Task.CompletedTask;
    }

    public ChannelReader<Envelope> Connect(out Guid clientId)
    {
        clientId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<Envelope>();
        _clients[clientId] = channel.Writer;
        return channel.Reader;
    }

    public void Disconnect(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var writer))
        {
            writer.TryComplete();
        }
    }
}
