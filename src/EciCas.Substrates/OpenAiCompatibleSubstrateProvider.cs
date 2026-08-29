using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Substrates;

/// <summary>
/// The one live substrate provider: talks to any OpenAI-compatible
/// /chat/completions endpoint. HttpClient's BaseAddress and bearer token are
/// configured once at DI registration time (see Program.cs) from
/// SubstrateProviderOptions plus the environment variable it names.
/// </summary>
public sealed class OpenAiCompatibleSubstrateProvider : ISubstrateProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SubstrateProviderOptions _options;

    public OpenAiCompatibleSubstrateProvider(HttpClient http, IOptions<SubstrateProviderOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var model = _options.Models.GetValueOrDefault(substrateClass, substrateClass);
        var request = new ChatCompletionRequest(model, [new ChatMessage("user", prompt)]);

        var started = Stopwatch.GetTimestamp();
        using var response = await _http.PostAsJsonAsync("chat/completions", request, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Substrate returned an empty completion response.");
        var elapsed = Stopwatch.GetElapsedTime(started);

        var text = payload.Choices.Count > 0 ? payload.Choices[0].Message.Content : string.Empty;
        var tokens = payload.Usage?.TotalTokens;
        var cost = tokens is int t ? t * _options.CostPerTokenUsd : (decimal?)null;

        return new SubstrateResult(text, elapsed, tokens, cost);
    }

    private sealed record ChatCompletionRequest(string Model, ChatMessage[] Messages);
    private sealed record ChatMessage(string Role, string Content);
    private sealed record ChatCompletionResponse(List<ChatChoice> Choices, ChatUsage? Usage);
    private sealed record ChatChoice(ChatMessage Message);

    private sealed record ChatUsage([property: JsonPropertyName("total_tokens")] int TotalTokens);
}
