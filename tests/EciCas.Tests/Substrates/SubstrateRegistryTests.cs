using System.Net;
using System.Text;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Substrates;

public class SubstrateRegistryTests
{
    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private static OpenAiCompatibleSubstrateProvider CreateLiveProvider(string json, SubstrateOptions options)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var http = new HttpClient(new FakeHandler(response)) { BaseAddress = new Uri("https://substrate.test/") };
        return new OpenAiCompatibleSubstrateProvider(http, Options.Create(options));
    }

    /// <summary>Resolves exactly one instance for the given key, mirroring DI
    /// resolving a keyed singleton — enough to test SubstrateRegistry without a real container.</summary>
    private sealed class SingleKeyedServiceProvider(string key, object instance) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(instance) ? instance : null;

        public object? GetKeyedService(Type serviceType, object? serviceKey) =>
            Equals(serviceKey, key) && serviceType.IsInstanceOfType(instance) ? instance : null;

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey) ?? throw new InvalidOperationException($"No service for key '{serviceKey}'.");
    }

    [Fact]
    public async Task WhenClassIsUnlistedOrMock_RoutesToMockProvider()
    {
        var options = new SubstrateOptions { Classes = { ["fast-low"] = new SubstrateClassEntry { Provider = "mock" } } };
        var live = CreateLiveProvider("""{"choices":[],"usage":null}""", options);
        var registry = new SubstrateRegistry(Options.Create(options), new MockSubstrateProvider(), new SingleKeyedServiceProvider("openai", live));

        var result = await registry.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.StartsWith("[mock:", result.Text);
    }

    [Fact]
    public async Task WhenClassNamesALiveProvider_RoutesToThatProvider()
    {
        const string json = """{"choices":[{"message":{"role":"assistant","content":"live answer"}}],"usage":{"total_tokens":42}}""";
        var options = new SubstrateOptions { Classes = { ["fast-high"] = new SubstrateClassEntry { Provider = "openai" } } };
        var live = CreateLiveProvider(json, options);
        var registry = new SubstrateRegistry(Options.Create(options), new MockSubstrateProvider(), new SingleKeyedServiceProvider("openai", live));

        var result = await registry.CompleteAsync("fast-high", "hello", CancellationToken.None);

        Assert.Equal("live answer", result.Text);
        Assert.Equal(42, result.TokenCount);
    }

    [Fact]
    public async Task DifferentClasses_CanRouteToDifferentLiveProviders()
    {
        const string mistralJson = """{"choices":[{"message":{"role":"assistant","content":"mistral answer"}}],"usage":{"total_tokens":10}}""";
        var options = new SubstrateOptions
        {
            Classes =
            {
                ["fast-low"] = new SubstrateClassEntry { Provider = "mistral" },
                ["slow-medium"] = new SubstrateClassEntry { Provider = "openai" },
            },
        };
        var mistral = CreateLiveProvider(mistralJson, options);
        var registry = new SubstrateRegistry(Options.Create(options), new MockSubstrateProvider(), new SingleKeyedServiceProvider("mistral", mistral));

        var result = await registry.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.Equal("mistral answer", result.Text);
    }
}
