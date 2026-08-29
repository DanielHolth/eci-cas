using System.Threading.Channels;

namespace EciCas.Core;

/// <summary>
/// Topic with per-subscriber queue. Publish never blocks on a subscriber —
/// there is no awaitable to accidentally await. Supports Topics.All as a
/// wildcard subscription.
/// </summary>
public interface IMessageBus
{
    void Publish(string topic, Envelope envelope);

    ChannelReader<Envelope> Subscribe(string topic);
}
