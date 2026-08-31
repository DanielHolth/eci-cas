using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Substrates;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint — OpenAI,
/// Mistral, Ollama, or anything else that speaks the same wire format. One
/// instance per configured provider, keyed by provider name (see
/// Program.cs); its HttpClient's BaseAddress and bearer token are bound at
/// DI-registration time from that provider's ProviderEndpoint entry.
/// </summary>
public sealed class OpenAiCompatibleSubstrateProvider : ISubstrateProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOptions<SubstrateOptions> _options;

    public OpenAiCompatibleSubstrateProvider(HttpClient http, IOptions<SubstrateOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var classEntry = options.Classes.GetValueOrDefault(substrateClass);
        var model = classEntry?.Model ?? substrateClass;
        var request = new ChatCompletionRequest(model, [new ChatMessage("user", prompt)], classEntry?.Effort);

        var started = Stopwatch.GetTimestamp();
        using var response = await _http.PostAsJsonAsync("chat/completions", request, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Substrate call to '{substrateClass}' failed: {(int)response.StatusCode} {response.StatusCode} — {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Substrate returned an empty completion response.");
        var elapsed = Stopwatch.GetElapsedTime(started);

        var text = payload.Choices.Count > 0 ? payload.Choices[0].Message.Content : string.Empty;
        var tokens = payload.Usage?.TotalTokens;
        var cost = tokens is int t ? t * (classEntry?.CostPerTokenUsd ?? 0m) : (decimal?)null;

        return new SubstrateResult(text, elapsed, tokens, cost);
    }

    private sealed record ChatCompletionRequest(
        string Model,
        ChatMessage[] Messages,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null);
    private sealed record ChatMessage(string Role, string Content);
    private sealed record ChatCompletionResponse(List<ChatChoice> Choices, ChatUsage? Usage);
    private sealed record ChatChoice(ChatMessage Message);

    private sealed record ChatUsage([property: JsonPropertyName("total_tokens")] int TotalTokens);
}
