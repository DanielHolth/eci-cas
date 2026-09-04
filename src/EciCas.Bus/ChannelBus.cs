using System.Collections.Concurrent;
using System.Threading.Channels;
using EciCas.Core;

namespace EciCas.Bus;

/// <summary>
/// Topic with per-subscriber queue. Publish does TryWrite to every matching
/// writer (exact-topic subscribers plus Topics.All wildcard subscribers) and
/// returns immediately — it never awaits a subscriber. Unbounded channels by
/// design: a bounded channel with backpressure would reintroduce the
/// publisher-blocks-on-subscriber bug this rebuild exists to fix.
/// </summary>
public sealed class ChannelBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, List<ChannelWriter<Envelope>>> _subscribers = new();
    private readonly BusActivityTracker _activity;

    public ChannelBus(BusActivityTracker activity) => _activity = activity;

    public void Publish(string topic, Envelope envelope)
    {
        if (_subscribers.TryGetValue(topic, out var exact))
        {
            lock (exact)
            {
                foreach (var writer in exact)
                {
                    // Counted only once the write took. Unreachable while the
                    // channels are unbounded, but the tracker is what tells
                    // the display the bus is idle, and a leaked count would
                    // leave it busy forever.
                    if (writer.TryWrite(envelope))
                    {
                        _activity.OnEnqueue();
                    }
                }
            }
        }

        if (topic != Topics.All && _subscribers.TryGetValue(Topics.All, out var wildcard))
        {
            lock (wildcard)
            {
                foreach (var writer in wildcard)
                {
                    // Counted only once the write took. Unreachable while the
                    // channels are unbounded, but the tracker is what tells
                    // the display the bus is idle, and a leaked count would
                    // leave it busy forever.
                    if (writer.TryWrite(envelope))
                    {
                        _activity.OnEnqueue();
                    }
                }
            }
        }
    }

    public ChannelReader<Envelope> Subscribe(string topic)
    {
        var channel = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
        {
            // Not SingleReader: AgentBase.WorkerCount can spin more than one
            // ConsumeAsync loop over the same reader.
            SingleReader = false,
            SingleWriter = false,
        });

        var writers = _subscribers.GetOrAdd(topic, static _ => []);
        lock (writers)
        {
            writers.Add(channel.Writer);
        }

        return channel.Reader;
    }
}
