using System.Collections.Concurrent;
using System.Threading.Channels;
using EciCas.Agents.Perception;
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
/// Clients may subscribe to one profile, in which case they see only that
/// profile's turns — one person's conversation shouldn't render in another
/// person's window. Only the perception envelope carries a profile id
/// (Derive() replaces meta rather than inheriting it), so the profile of a
/// turn is learned once, from its perception, and every later envelope is
/// matched by CorrelationId. That map is a display concern and stays here,
/// in the display layer: no agent gains a field for it.
/// </summary>
public sealed class SseBroadcaster : AgentBase
{
    /// <summary>Turns whose profile is known, newest last — bounded, since this only ever needs the turns a live client might still be assembling.</summary>
    private const int TrackedTurns = 200;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentDictionary<Guid, string> _turnProfiles = new();
    private readonly Queue<Guid> _turnOrder = new();
    private readonly object _turnLock = new();

    private sealed record Client(ChannelWriter<Envelope> Writer, string? ProfileId);

    public SseBroadcaster(IMessageBus bus, BusActivityTracker activity, ILogger<SseBroadcaster> logger)
        : base(bus, activity, logger)
    {
    }

    public override string Name => "SseBroadcaster";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Topic == Topics.Perception
            && envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) is { Length: > 0 } profileId)
        {
            Remember(envelope.CorrelationId, profileId);
        }

        foreach (var client in _clients.Values)
        {
            if (WantsTurn(client, envelope))
            {
                client.Writer.TryWrite(envelope);
            }
        }

        return Task.CompletedTask;
    }

    public ChannelReader<Envelope> Connect(string? profileId, out Guid clientId)
    {
        clientId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<Envelope>();
        _clients[clientId] = new Client(channel.Writer, profileId);
        return channel.Reader;
    }

    public void Disconnect(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            client.Writer.TryComplete();
        }
    }

    /// <summary>
    /// A client that named no profile sees everything, as before. One that
    /// did sees its own turns, plus turns nobody owns — the console loop and
    /// Reflection's self-generated ideas belong to the whole device, and
    /// hiding the persona's own thinking from every window would leave it
    /// visible in none of them.
    /// </summary>
    private bool WantsTurn(Client client, Envelope envelope)
    {
        if (client.ProfileId is null)
        {
            return true;
        }

        return !_turnProfiles.TryGetValue(envelope.CorrelationId, out var owner)
            || string.Equals(owner, client.ProfileId, StringComparison.Ordinal);
    }

    private void Remember(Guid correlationId, string profileId)
    {
        lock (_turnLock)
        {
            if (!_turnProfiles.TryAdd(correlationId, profileId))
            {
                return;
            }

            _turnOrder.Enqueue(correlationId);
            while (_turnOrder.Count > TrackedTurns)
            {
                _turnProfiles.TryRemove(_turnOrder.Dequeue(), out _);
            }
        }
    }
}
