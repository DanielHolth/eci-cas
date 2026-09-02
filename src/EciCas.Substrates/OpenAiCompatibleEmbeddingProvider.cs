using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Substrates;

using EciCas.Core;

/// <summary>
/// /v1/embeddings against the same OpenAI-compatible endpoint
/// OpenAiCompatibleSubstrateProvider talks to, sharing its named HttpClient
/// so BaseUrl, timeout and the API-key environment variable are configured in
/// exactly one place.
///
/// A failed call returns nothing rather than throwing, for the same reason
/// the ONNX provider tolerates missing weights: retrieval that didn't happen
/// degrades the turn to the pre-vector path, which is a worse answer, not a
/// broken one.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger _logger;

    public OpenAiCompatibleEmbeddingProvider(HttpClient http, IOptions<EmbeddingOptions> options, ILogger<OpenAiCompatibleEmbeddingProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool Available => true;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        try
        {
            var response = await _http.PostAsJsonAsync("embeddings", new Request(_options.ApiModel, texts), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<Response>(cancellationToken).ConfigureAwait(false);

            // Index order is not guaranteed to be response order; the API
            // returns the index it belongs to, so honour it.
            return [.. (body?.Data ?? [])
                .OrderBy(d => d.Index)
                .Select(d => VectorMath.Normalize(d.Embedding))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Embedding call {Cause}, passage retrieval skipped this turn", SubstrateHealth.Classify(ex));
            return [];
        }
    }

    private sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record Response([property: JsonPropertyName("data")] IReadOnlyList<Datum> Data);

    private sealed record Datum(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
