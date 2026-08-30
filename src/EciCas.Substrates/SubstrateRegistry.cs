using EciCas.Core;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _services;

    // OpenAiCompatibleSubstrateProvider is a typed HttpClient (AddHttpClient<T>),
    // registered transient on purpose so IHttpClientFactory can rotate its
    // handler. Resolving it per call (rather than capturing one instance in
    // this singleton's constructor) keeps that rotation working instead of
    // pinning one HttpMessageHandler for the process's lifetime.
    public SubstrateRegistry(IOptions<BudgetOptions> budget, MockSubstrateProvider mock, IServiceProvider services)
    {
        _budget = budget.Value;
        _mock = mock;
        _services = services;
    }

    public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var tier = _budget.Tiers.GetValueOrDefault(substrateClass, "mock");
        ISubstrateProvider provider = tier == "live"
            ? _services.GetRequiredService<OpenAiCompatibleSubstrateProvider>()
            : _mock;
        return provider.CompleteAsync(substrateClass, prompt, cancellationToken);
    }
}
