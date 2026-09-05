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

    /// <summary>Null when the provider is ungated, which is every vendor API.</summary>
    private readonly SemaphoreSlim? _slots;

    /// <summary>Tick count until which this provider is considered dead; 0 means closed.</summary>
    private long _openUntil;

    public OpenAiCompatibleSubstrateProvider(HttpClient http, IOptions<SubstrateOptions> options, TimeSpan circuitOpen = default, int maxConcurrent = 0)
    {
        _http = http;
        _options = options;
        _circuitOpen = circuitOpen;
        _slots = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent, maxConcurrent) : null;
    }

    public async Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var classEntry = options.Classes.GetValueOrDefault(substrateClass);
        var model = classEntry?.Model ?? substrateClass;
        var request = new ChatCompletionRequest(
            model,
            [new ChatMessage("user", prompt)],
            classEntry?.Effort,
            classEntry?.MaxTokens,
            classEntry?.Thinking is bool thinking ? new ChatTemplateKwargs(thinking) : null);

        // Fail fast while the circuit is open. One dead endpoint would
        // otherwise cost every agent in the fan-out a full timeout each,
        // turning a broken turn into a very slow broken turn.
        if (Interlocked.Read(ref _openUntil) is var until && until > Environment.TickCount64)
        {
            throw new HttpRequestException($"Substrate provider is circuit-open for another {until - Environment.TickCount64}ms.");
        }

        // Timed from before the queue rather than after: the wait is part of
        // what the turn cost, and a trace that hid it would make a saturated
        // local server look fast right up until someone complained.
        var started = Stopwatch.GetTimestamp();

        if (_slots is not null)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

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
        finally
        {
            _slots?.Release();
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

        var text = StripThinking(payload.Choices.Count > 0 ? payload.Choices[0].Message.Content : string.Empty);
        var tokens = payload.Usage?.TotalTokens;
        var cost = tokens is int t ? t * (classEntry?.CostPerTokenUsd ?? 0m) : (decimal?)null;

        return new SubstrateResult(text, elapsed, tokens, cost);
    }

    /// <summary>
    /// Drops a leading reasoning block. A model told not to think can still
    /// emit one, and every parser downstream expects the answer to start at
    /// the first character — Recall's whole reply is meant to be a number.
    /// An unterminated block means the output ran into MaxTokens before the
    /// answer arrived; nothing usable is left, so it all goes.
    /// </summary>
    private static string StripThinking(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (!trimmed.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var end = trimmed.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : trimmed[(end + "</think>".Length)..].TrimStart().ToString();
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
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
        [property: JsonPropertyName("max_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens = null,
        [property: JsonPropertyName("chat_template_kwargs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatTemplateKwargs? ChatTemplateKwargs = null);

    private sealed record ChatTemplateKwargs(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking);
    private sealed record ChatMessage(string Role, string Content);
    private sealed record ChatCompletionResponse(List<ChatChoice> Choices, ChatUsage? Usage);
    private sealed record ChatChoice(ChatMessage Message);

    private sealed record ChatUsage([property: JsonPropertyName("total_tokens")] int TotalTokens);
}
