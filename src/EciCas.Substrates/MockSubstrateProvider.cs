using EciCas.Core;

namespace EciCas.Substrates;

/// <summary>
/// Zero-cost, zero-dependency substrate for the "mock" budget tier — every
/// substrate class resolves here until a manifest entry opts into "live".
/// </summary>
public sealed class MockSubstrateProvider : ISubstrateProvider
{
    public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var result = new SubstrateResult($"[mock:{substrateClass}] {prompt}", TimeSpan.FromMilliseconds(5), prompt.Length / 4, 0m);
        return Task.FromResult(result);
    }
}
