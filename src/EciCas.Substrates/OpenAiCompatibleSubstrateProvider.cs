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
    private readonly TimeSpan _circuitOpen;

    /// <summary>Tick count until which this provider is considered dead; 0 means closed.</summary>
    private long _openUntil;

    public OpenAiCompatibleSubstrateProvider(HttpClient http, IOptions<SubstrateOptions> options, TimeSpan circuitOpen = default)
    {
        _http = http;
        _options = options;
        _circuitOpen = circuitOpen;
    }

    public async Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var classEntry = options.Classes.GetValueOrDefault(substrateClass);
        var model = classEntry?.Model ?? substrateClass;
        var request = new ChatCompletionRequest(model, [new ChatMessage("user", prompt)], classEntry?.Effort);

        // Fail fast while the circuit is open. One dead endpoint would
        // otherwise cost every agent in the fan-out a full timeout each,
        // turning a broken turn into a very slow broken turn.
        if (Interlocked.Read(ref _openUntil) is var until && until > Environment.TickCount64)
        {
            throw new HttpRequestException($"Substrate provider is circuit-open for another {until - Environment.TickCount64}ms.");
        }

        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("chat/completions", request, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Trip();
            throw;
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Substrate call to '{substrateClass}' failed: {(int)response.StatusCode} {response.StatusCode} — {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Substrate returned an empty completion response.");
        var elapsed = Stopwatch.GetElapsedTime(started);

        // A reply of any shape means the endpoint is alive again.
        Interlocked.Exchange(ref _openUntil, 0);

        var text = payload.Choices.Count > 0 ? payload.Choices[0].Message.Content : string.Empty;
        var tokens = payload.Usage?.TotalTokens;
        var cost = tokens is int t ? t * (classEntry?.CostPerTokenUsd ?? 0m) : (decimal?)null;

        return new SubstrateResult(text, elapsed, tokens, cost);
    }

    private void Trip()
    {
        if (_circuitOpen > TimeSpan.Zero)
        {
            Interlocked.Exchange(ref _openUntil, Environment.TickCount64 + (long)_circuitOpen.TotalMilliseconds);
        }
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
