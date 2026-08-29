using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Substrates;

/// <summary>
/// The registry: the single ISubstrateProvider every CognitiveAgent depends
/// on. Routes each call by substrate class to the tier's provider (defaulting
/// unlisted classes to "mock" — the safe, zero-cost choice). Adding a provider
/// is this class plus one DI line, never a change to any agent.
/// </summary>
public sealed class SubstrateRegistry : ISubstrateProvider
{
    private readonly BudgetOptions _budget;
    private readonly MockSubstrateProvider _mock;
    private readonly OpenAiCompatibleSubstrateProvider _live;

    public SubstrateRegistry(IOptions<BudgetOptions> budget, MockSubstrateProvider mock, OpenAiCompatibleSubstrateProvider live)
    {
        _budget = budget.Value;
        _mock = mock;
        _live = live;
    }

    public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var tier = _budget.Tiers.GetValueOrDefault(substrateClass, "mock");
        ISubstrateProvider provider = tier == "live" ? _live : _mock;
        return provider.CompleteAsync(substrateClass, prompt, cancellationToken);
    }
}
