using EciCas.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Substrates;

/// <summary>
/// The registry: the single ISubstrateProvider every CognitiveAgent depends
/// on. Routes each call by substrate class to that class's configured
/// provider (defaulting unlisted classes to "mock" — the safe, zero-cost
/// choice). Adding a live provider is a Program.cs registration plus a
/// manifest entry, never a change to any agent.
/// </summary>
public sealed class SubstrateRegistry : ISubstrateProvider
{
    private readonly SubstrateOptions _options;
    private readonly MockSubstrateProvider _mock;
    private readonly IServiceProvider _services;

    // Live providers are keyed singletons (see Program.cs), one per
    // configured provider name, each wrapping its own typed HttpClient so
    // IHttpClientFactory can rotate handlers per provider independently.
    public SubstrateRegistry(IOptions<SubstrateOptions> options, MockSubstrateProvider mock, IServiceProvider services)
    {
        _options = options.Value;
        _mock = mock;
        _services = services;
    }

    public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken) =>
        For(agent).CompleteAsync(agent, prompt, cancellationToken);

    /// <summary>
    /// Routed identically. A provider with no eyes falls back to the
    /// text-only call through the interface's own default, so pointing Sight
    /// at a text model is a blind answer rather than a crash — which is what
    /// the Free tier does on purpose.
    /// </summary>
    public Task<SubstrateResult> CompleteAsync(string agent, string prompt, SubstrateImage? image, CancellationToken cancellationToken) =>
        For(agent).CompleteAsync(agent, prompt, image, cancellationToken);

    private ISubstrateProvider For(string agent)
    {
        var providerName = _options.Agents.GetValueOrDefault(agent)?.Provider ?? "mock";
        return providerName == "mock"
            ? _mock
            : _services.GetRequiredKeyedService<ISubstrateProvider>(providerName);
    }
}
