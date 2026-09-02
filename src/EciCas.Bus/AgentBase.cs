using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EciCas.Core;

namespace EciCas.Bus;

/// <summary>
/// Owns its subscriptions, its channel readers, and its consumer loop(s). One
/// agent's exception never reaches another — caught, logged, and the loop
/// continues. WorkerCount lets a subclass fan out internally (its own job,
/// never coupling to another agent's queue).
/// </summary>
public abstract class AgentBase : BackgroundService, IAgent
{
    private readonly IMessageBus _bus;
    private readonly BusActivityTracker _activity;
    private readonly ILogger _logger;

    protected AgentBase(IMessageBus bus, BusActivityTracker activity, ILogger logger)
    {
        _bus = bus;
        _activity = activity;
        _logger = logger;
    }

    public abstract string Name { get; }
    public abstract IReadOnlyCollection<string> Subscriptions { get; }
    public abstract Task HandleAsync(Envelope envelope, CancellationToken cancellationToken);

    protected virtual int WorkerCount => 1;

    // Subscribing is what claims a queue; consuming only drains it. Those
    // have to happen at different times: ChannelBus drops a publish that
    // arrives before the subscriber exists, silently and with no error, so
    // an agent whose queue is created inside ExecuteAsync misses every
    // envelope published between host startup and its own loop spinning up.
    // StartAsync runs to completion for each hosted service in registration
    // order, so claiming the queue here means every agent is subscribed
    // before any of them can publish.
    private List<System.Threading.Channels.ChannelReader<Envelope>>? _readers;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _readers = Subscriptions.Select(_bus.Subscribe).ToList();
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Non-null in the normal hosted path; the fallback keeps a directly
        // constructed agent (some tests) working rather than throwing.
        var readers = _readers ??= Subscriptions.Select(_bus.Subscribe).ToList();

        var workers = readers
            .SelectMany(reader => Enumerable.Range(0, WorkerCount).Select(_ => ConsumeAsync(reader, stoppingToken)));

        return Task.WhenAll(workers);
    }

    private async Task ConsumeAsync(System.Threading.Channels.ChannelReader<Envelope> reader, CancellationToken stoppingToken)
    {
        await foreach (var envelope in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Agent} failed handling {Topic} event {EventId}", Name, envelope.Topic, envelope.EventId);
            }
            finally
            {
                _activity.OnHandled();
            }
        }
    }
}
