using System.Collections.Concurrent;
using EciCas.Core;
using EciCas.Substrates;

namespace EciCas.Host;

/// <summary>
/// One throwaway completion per distinct backing model, at boot.
///
/// The first call to a provider pays for things that have nothing to do with
/// thinking: DNS, TCP and TLS for a named HttpClient that has never been
/// used, and — on a local server — loading several gigabytes of weights off
/// disk. Left alone, all of that lands on the first turn a person types,
/// which is exactly the turn they are judging the persona on.
///
/// Deduplicated by provider+model rather than by agent: the free tier
/// points every agent at one local 4B, and warming that model eight times
/// would move the wait rather than remove it. The mock
/// provider is skipped — it has nothing to warm.
///
/// Nothing here can fail a boot. A provider that is down at startup is
/// already handled everywhere else (circuit breaker, degraded marking), and
/// a warm-up that refuses to start the host would turn a latency
/// optimisation into an availability regression.
/// </summary>
public static class SubstrateWarmup
{
    /// <summary>
    /// Short enough to answer in one token on any model, and phrased so a
    /// model that ignores it still stops quickly.
    /// </summary>
    private const string Prompt = "Reply with one word: ok";

    /// <summary>
    /// provider/model -> why its last warm-up failed. A timeout is not a
    /// failure here: a local model still loading off disk would read as down.
    /// Only models a warm-up has tried appear, so a tier never switched to
    /// stays unknown rather than claimed healthy.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Failures = new(StringComparer.OrdinalIgnoreCase);

    public static string ModelName(string agent, SubstrateAgentEntry entry) => $"{entry.Provider}/{entry.Model ?? agent}";

    public static string? FailureFor(string modelName) => Failures.TryGetValue(modelName, out var why) ? why : null;

    public static async Task RunAsync(
        ISubstrateProvider substrates,
        SubstrateOptions options,
        TimeSpan budget,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        var firstAgentPerModel = options.Agents
            .Where(c => !string.Equals(c.Value.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (c.Value.Provider, c.Value.Model ?? c.Key))
            .Select(g => g.First())
            .ToList();

        if (firstAgentPerModel.Count == 0)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        // Sequential on purpose. Distinct models are usually distinct
        // vendors, but when they are not — two models on one local server —
        // loading them at once is how a warm-up becomes an out-of-memory.
        foreach (var entry in firstAgentPerModel)
        {
            var name = ModelName(entry.Key, entry.Value);
            var started = DateTimeOffset.UtcNow;
            try
            {
                await substrates.CompleteAsync(entry.Key, Prompt, cts.Token);
                Failures.TryRemove(name, out _);
                report($"warm: {name} in {(DateTimeOffset.UtcNow - started).TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                var why = SubstrateHealth.Classify(ex);
                if (why == SubstrateHealth.TimedOut)
                {
                    Failures.TryRemove(name, out _);
                }
                else
                {
                    Failures[name] = why;
                }

                report($"warm: {name} skipped ({why})");
            }
        }
    }
}
