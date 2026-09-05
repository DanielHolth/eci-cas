using System.Net;
using System.Text;
using EciCas.Substrates;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Substrates;

/// <summary>
/// The wire shape and the concurrency gate. Both matter mostly for the local
/// tier — one model behind every class — but the absence assertions are what
/// protect the vendor tiers from picking up fields they never asked for.
/// </summary>
public class OpenAiCompatibleSubstrateProviderTests
{
    private const string Ok = """{"choices":[{"message":{"role":"assistant","content":"answer"}}],"usage":{"total_tokens":7}}""";

    /// <summary>Records each request body and optionally holds calls open so overlap is observable.</summary>
    private sealed class RecordingHandler(string json, TimeSpan hold = default) : HttpMessageHandler
    {
        private int _inFlight;

        public List<string> Bodies { get; } = [];
        public int PeakInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(body);
                PeakInFlight = Math.Max(PeakInFlight, ++_inFlight);
            }

            if (hold > TimeSpan.Zero)
            {
                await Task.Delay(hold, cancellationToken);
            }

            lock (Bodies)
            {
                _inFlight--;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static OpenAiCompatibleSubstrateProvider Create(RecordingHandler handler, SubstrateOptions options, int maxConcurrent = 0) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://substrate.test/") },
            Options.Create(options),
            TimeSpan.Zero,
            maxConcurrent);

    private static SubstrateOptions WithClass(SubstrateClassEntry entry) =>
        new() { Classes = { ["fast-low"] = entry } };

    [Fact]
    public async Task WhenClassSetsNeither_BodyCarriesNoLocalOnlyFields()
    {
        var handler = new RecordingHandler(Ok);
        var provider = Create(handler, WithClass(new SubstrateClassEntry { Provider = "openai" }));

        await provider.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.DoesNotContain("max_tokens", handler.Bodies[0]);
        Assert.DoesNotContain("chat_template_kwargs", handler.Bodies[0]);
    }

    [Fact]
    public async Task WhenClassSetsMaxTokensAndThinking_BothReachTheBody()
    {
        var handler = new RecordingHandler(Ok);
        var provider = Create(handler, WithClass(new SubstrateClassEntry { Provider = "local", MaxTokens = 512, Thinking = false }));

        await provider.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.Contains("\"max_tokens\":512", handler.Bodies[0]);
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false}", handler.Bodies[0]);
    }

    [Fact]
    public async Task WhenMaxConcurrentIsSet_CallsQueueInsteadOfOverlapping()
    {
        var handler = new RecordingHandler(Ok, TimeSpan.FromMilliseconds(50));
        var provider = Create(handler, WithClass(new SubstrateClassEntry { Provider = "local" }), maxConcurrent: 2);

        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => provider.CompleteAsync("fast-low", "hello", CancellationToken.None)));

        Assert.Equal(6, handler.Bodies.Count);
        Assert.True(handler.PeakInFlight <= 2, $"peak in-flight was {handler.PeakInFlight}");
    }

    [Fact]
    public async Task WhenMaxConcurrentIsZero_CallsAreUngated()
    {
        var handler = new RecordingHandler(Ok, TimeSpan.FromMilliseconds(50));
        var provider = Create(handler, WithClass(new SubstrateClassEntry { Provider = "openai" }));

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => provider.CompleteAsync("fast-low", "hello", CancellationToken.None)));

        Assert.True(handler.PeakInFlight > 1, "an ungated provider should have overlapped at least once");
    }

    [Theory]
    [InlineData("<think>weighing it up</think>2", "2")]
    [InlineData("  <think>hm</think>\n  the answer", "the answer")]
    [InlineData("no thinking here", "no thinking here")]
    [InlineData("<think>ran out of room before answering", "")]
    public async Task LeadingReasoningBlockIsStripped(string content, string expected)
    {
        var json = """{"choices":[{"message":{"role":"assistant","content":"""
            + System.Text.Json.JsonSerializer.Serialize(content)
            + """}}],"usage":null}""";
        var provider = Create(new RecordingHandler(json), WithClass(new SubstrateClassEntry { Provider = "local" }));

        var result = await provider.CompleteAsync("fast-low", "hello", CancellationToken.None);

        Assert.Equal(expected, result.Text);
    }
}
