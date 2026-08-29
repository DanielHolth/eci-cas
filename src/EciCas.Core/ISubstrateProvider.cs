namespace EciCas.Core;

/// <summary>
/// Logical substrate class (fast-low/fast-medium/fast-high, slow-low/slow-medium/slow-high)
/// resolved to a concrete completion. The tier is a manifest/DI choice — a mock
/// is a substrate, not a separate agent class. Implemented in M2.
/// </summary>
public interface ISubstrateProvider
{
    Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken);
}

public sealed record SubstrateResult(string Text, TimeSpan Latency, int? TokenCount, decimal? Cost);
