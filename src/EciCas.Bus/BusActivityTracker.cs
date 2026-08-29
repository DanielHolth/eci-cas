namespace EciCas.Bus;

/// <summary>
/// Counts in-flight deliveries so tests can await quiescence instead of
/// Thread.Sleep. Incremented when ChannelBus enqueues a delivery, decremented
/// by AgentBase after each envelope's handler completes (success or failure).
/// </summary>
public sealed class BusActivityTracker
{
    private long _inFlight;

    public void OnEnqueue() => Interlocked.Increment(ref _inFlight);

    public void OnHandled() => Interlocked.Decrement(ref _inFlight);

    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (Interlocked.Read(ref _inFlight) != 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, cts.Token).ConfigureAwait(false);
        }
    }
}
