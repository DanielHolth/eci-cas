using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Substrates;

public class CachingEmbeddingProviderTests
{
    private sealed class CountingProvider(bool available = true) : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public int TextsEmbedded { get; private set; }
        public bool Available => available;
        public string ModelId => "onnx:test";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls++;
            TextsEmbedded += texts.Count;
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(t => new[] { (float)t.Length, 1f })]);
        }
    }

    private static CachingEmbeddingProvider Wrap(IEmbeddingProvider inner) =>
        new(inner, NullLogger<CachingEmbeddingProvider>.Instance);

    /// <summary>
    /// The case this type exists for: Librarian and Hindsight embed the same
    /// perception text on the same turn, and the ONNX session serializes, so
    /// the second was waiting for the first and then recomputing it.
    /// </summary>
    [Fact]
    public async Task SameTextTwice_CostsOneModelPass()
    {
        var inner = new CountingProvider();
        var provider = Wrap(inner);

        var first = await provider.EmbedAsync(["what did we say about the trip"], CancellationToken.None);
        var second = await provider.EmbedAsync(["what did we say about the trip"], CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first[0], second[0]);
    }

    /// <summary>
    /// Every caller got a private array before this type existed. Handing out
    /// the cached instance would alias two agents' vectors to one buffer.
    /// </summary>
    [Fact]
    public async Task CachedVectorsAreNotShared()
    {
        var provider = Wrap(new CountingProvider());

        var first = await provider.EmbedAsync(["a turn"], CancellationToken.None);
        var second = await provider.EmbedAsync(["a turn"], CancellationToken.None);

        Assert.NotSame(first[0], second[0]);

        first[0][0] = 99f;
        var third = await provider.EmbedAsync(["a turn"], CancellationToken.None);
        Assert.NotEqual(99f, third[0][0]);
    }

    [Fact]
    public async Task OnlyTheUncachedTextsReachTheModel()
    {
        var inner = new CountingProvider();
        var provider = Wrap(inner);

        await provider.EmbedAsync(["one", "two"], CancellationToken.None);
        await provider.EmbedAsync(["two", "three"], CancellationToken.None);

        Assert.Equal(3, inner.TextsEmbedded);
    }

    /// <summary>
    /// Absence of an embedder is a normal state, not a degradation, so the
    /// wrapper has to pass it through rather than manufacture empty vectors.
    /// </summary>
    [Fact]
    public async Task WhenInnerIsUnavailable_ReturnsNothingAndNeverCalls()
    {
        var inner = new CountingProvider(available: false);
        var provider = Wrap(inner);

        Assert.False(provider.Available);
        Assert.Empty(await provider.EmbedAsync(["a turn"], CancellationToken.None));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task ModelIdPassesThrough()
    {
        var provider = Wrap(new CountingProvider());
        Assert.Equal("onnx:test", provider.ModelId);
        await Task.CompletedTask;
    }

    /// <summary>
    /// A cache is allowed to forget; a caller is not allowed to notice.
    /// Answers used to be read back out of the cache after storing, so a call
    /// wider than Capacity evicted its own earliest entries before the
    /// projection reached them and threw KeyNotFoundException. Nothing batches
    /// this wide today, which is exactly why it needs a test — the first
    /// caller to backfill a corpus would have found it in production.
    /// </summary>
    [Fact]
    public async Task MoreDistinctTextsThanCapacity_StillAnswersAllOfThem()
    {
        var inner = new CountingProvider();
        var provider = Wrap(inner);
        var texts = Enumerable.Range(0, 40).Select(i => new string('x', i + 1)).ToList();

        var vectors = await provider.EmbedAsync(texts, CancellationToken.None);

        Assert.Equal(texts.Count, vectors.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            Assert.Equal(texts[i].Length, vectors[i][0]);
        }
    }

    /// <summary>Duplicates within one call are one model pass, and every position still gets its answer.</summary>
    [Fact]
    public async Task RepeatedTextWithinOneCall_IsEmbeddedOnceAndAnsweredEverywhere()
    {
        var inner = new CountingProvider();
        var provider = Wrap(inner);

        var vectors = await provider.EmbedAsync(["ab", "abc", "ab"], CancellationToken.None);

        Assert.Equal(2, inner.TextsEmbedded);
        Assert.Equal(3, vectors.Count);
        Assert.Equal(vectors[0], vectors[2]);
        Assert.NotSame(vectors[0], vectors[2]);
    }
}
