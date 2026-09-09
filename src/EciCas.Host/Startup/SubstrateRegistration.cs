using System.Net.Http.Headers;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// What the persona thinks with: one keyed provider per configured vendor,
/// a registry that resolves an agent name to one of them, and the embedder
/// the archive and the passage corpus are searched with.
/// </summary>
internal static class SubstrateRegistration
{
    public static IServiceCollection AddSubstrates(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MockSubstrateProvider>();

        // One named HttpClient + keyed ISubstrateProvider per configured live
        // provider (see Substrates:Providers in appsettings.json) — this is how
        // e.g. OpenAI and Mistral can both be live at once, each backing whichever
        // substrate classes name it as their Provider.
        foreach (var providerSection in configuration.GetSection("Substrates:Providers").GetChildren())
        {
            var providerName = providerSection.Key;
            var baseUrl = providerSection["BaseUrl"]
                ?? throw new InvalidOperationException($"Substrate provider '{providerName}' is missing BaseUrl.");
            var apiKeyEnvironmentVariable = providerSection["ApiKeyEnvironmentVariable"];
            var timeoutMs = int.TryParse(providerSection["TimeoutMs"], out var t) ? t : 20_000;
            var circuitOpen = TimeSpan.FromMilliseconds(int.TryParse(providerSection["CircuitOpenMs"], out var c) ? c : 5_000);
            var maxConcurrent = int.TryParse(providerSection["MaxConcurrent"], out var m) ? m : 0;

            services.AddHttpClient(providerName, http =>
            {
                http.BaseAddress = new Uri(baseUrl);
                http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                var apiKey = apiKeyEnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            })
            // The factory rotates handlers every two minutes by default, to pick up
            // DNS changes. That also throws away the connection SubstrateWarmup just
            // paid for, so a persona idle for three minutes pays the handshake again
            // on the turn someone finally types. PooledConnectionLifetime is the
            // supported way to keep the DNS refresh without the rotation: the pool
            // retires a connection on its own schedule, and the handler stays.
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            });

            services.AddKeyedSingleton<ISubstrateProvider>(providerName, (sp, key) =>
                new OpenAiCompatibleSubstrateProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient((string)key!),
                    sp.GetRequiredService<IOptions<SubstrateOptions>>(),
                    circuitOpen,
                    maxConcurrent));
        }

        services.AddSingleton<ISubstrateProvider, SubstrateRegistry>();

        // The embedder backing the passage corpus. Local ONNX by default so the
        // minimal tier keeps its memory offline; "openai" borrows the named
        // HttpClient a completion provider already configured, so BaseUrl and the
        // key's environment variable are declared exactly once.
        var embeddingProvider = configuration["Embedding:Provider"] ?? "onnx";
        Func<IServiceProvider, IEmbeddingProvider> embedderFactory;
        switch (embeddingProvider.ToLowerInvariant())
        {
            case "onnx":
                services.AddSingleton<OnnxEmbeddingProvider>();
                embedderFactory = sp => sp.GetRequiredService<OnnxEmbeddingProvider>();
                break;
            case "openai":
                var apiProvider = configuration["Embedding:ApiProvider"] ?? "openai";
                embedderFactory = sp => new OpenAiCompatibleEmbeddingProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(apiProvider),
                    sp.GetRequiredService<IOptions<EmbeddingOptions>>(),
                    sp.GetRequiredService<ILogger<OpenAiCompatibleEmbeddingProvider>>());
                break;
            // "api" is docs/architecture.md's spelling of the same thing and is
            // accepted so the two cannot disagree in silence.
            case "api":
                goto case "openai";
            case "none":
                embedderFactory = _ => new NullEmbeddingProvider();
                break;
            default:
                // Previously this branch was the default, so a typo turned the whole
                // passage corpus off without a word: no warning, no error, just a
                // persona that never remembers a thought and no way to tell that
                // from "the weights aren't downloaded yet". "none" still means none;
                // anything else is a mistake and says so.
                throw new InvalidOperationException(
                    $"Embedding:Provider is \"{embeddingProvider}\". Valid values are \"onnx\" (local weights), " +
                    "\"openai\" (an OpenAI-compatible embeddings endpoint, also spelled \"api\"), and \"none\".");
        }

        // Wrapped whichever way it was built. Librarian and Hindsight embed the same
        // perception text on the same turn, and the ONNX session serializes on a
        // lock, so the second was waiting for the first and then recomputing an
        // identical vector. Deduplicating here keeps both agents unaware of each
        // other and keeps the vector off the bus.
        services.AddSingleton<IEmbeddingProvider>(sp => new CachingEmbeddingProvider(
            embedderFactory(sp), sp.GetRequiredService<ILogger<CachingEmbeddingProvider>>()));

        return services;
    }
}
