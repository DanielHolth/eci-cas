using System.Net;
using System.Text;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Substrates;

public class SubstrateRegistryTests
{
    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private static OpenAiCompatibleSubstrateProvider CreateLiveProvider(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var http = new HttpClient(new FakeHandler(response)) { BaseAddress = new Uri("https://substrate.test/") };
        return new OpenAiCompatibleSubstrateProvider(http, Options.Create(new SubstrateProviderOptions()));
    }

    /// <summary>Resolves exactly one instance, mirroring DI resolving a typed
    /// HttpClient — enough to test SubstrateRegistry without a real container.</summary>
    private sealed class SingleInstanceServiceProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(instance) ? instance : null;
    }

    [Fact]
    public async Task WhenTierIsUnlistedOrMock_RoutesToMockProvider()
    {
        var budget = Options.Create(new BudgetOptions { Tiers = { ["fast-low"] = "mock" } });
        var live = CreateLiveProvider("""{"choices":[],"usage":null}""");
        var registry = new SubstrateRegistry(budget, new MockSubstrateProvider(), new SingleInstanceServiceProvider(live));

        var result = await registry.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.StartsWith("[mock:", result.Text);
    }

    [Fact]
    public async Task WhenTierIsLive_RoutesToLiveProvider()
    {
        const string json = """{"choices":[{"message":{"role":"assistant","content":"live answer"}}],"usage":{"total_tokens":42}}""";
        var budget = Options.Create(new BudgetOptions { Tiers = { ["fast-high"] = "live" } });
        var live = CreateLiveProvider(json);
        var registry = new SubstrateRegistry(budget, new MockSubstrateProvider(), new SingleInstanceServiceProvider(live));

        var result = await registry.CompleteAsync("fast-high", "hello", CancellationToken.None);

        Assert.Equal("live answer", result.Text);
        Assert.Equal(42, result.TokenCount);
    }
}
