using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// The write side of the row vectors: what the decorator stamps, what it
/// leaves alone, and what happens when there is no embedder to ask.
/// </summary>
public class EmbeddingArchiveStoreTests
{
    private static StubEmbeddings Counting() =>
        new(text => VectorMath.Normalize([text.Count(c => c == 'x'), 1f]));

    private static ArchiveRecord Row(string key, string value) =>
        new("person", "family", "sub", "subject", key, value, DateTimeOffset.UtcNow);

    [Fact]
    public async Task EveryWrittenRow_ArrivesWithACurrentVector()
    {
        var inner = new InMemoryArchiveStore();
        var embeddings = Counting();
        var store = new EmbeddingArchiveStore(inner, embeddings);

        await store.WriteAsync([Row("home", "oslo"), Row("work", "bergen")], CancellationToken.None);

        Assert.All(inner.All, r => Assert.True(r.HasVector(embeddings.ModelId)));
    }

    /// <summary>
    /// No embedder is the offline tier. The fact still lands; it just lands
    /// unembedded, which is the pre-vector behaviour and still works.
    /// </summary>
    [Fact]
    public async Task WithNoEmbedder_TheRowIsWrittenUntouched()
    {
        var inner = new InMemoryArchiveStore();
        var store = new EmbeddingArchiveStore(inner, new StubEmbeddings());

        await store.WriteAsync([Row("home", "oslo")], CancellationToken.None);

        var written = Assert.Single(inner.All);
        Assert.Null(written.Embedding);
        Assert.Equal("", written.EmbeddingModelId);
    }

    /// <summary>
    /// An embedder that throws must not take the write with it: a fact
    /// reaching the archive matters more than a fact reaching it searchable.
    /// </summary>
    [Fact]
    public async Task WhenTheEmbedderFails_TheFactIsStillFiled()
    {
        var inner = new InMemoryArchiveStore();
        var store = new EmbeddingArchiveStore(inner, new StubEmbeddings(_ => throw new HttpRequestException("no endpoint")));

        await store.WriteAsync([Row("home", "oslo")], CancellationToken.None);

        Assert.Null(Assert.Single(inner.All).Embedding);
    }

    /// <summary>
    /// A row that already carries a current vector is not re-embedded — which
    /// is what keeps restating one fact from paying for its neighbours.
    /// </summary>
    [Fact]
    public async Task ARowThatAlreadyCarriesACurrentVector_IsNotEmbeddedAgain()
    {
        var embedded = new List<string>();
        var embeddings = new StubEmbeddings(text =>
        {
            lock (embedded)
            {
                embedded.Add(text);
            }

            return VectorMath.Normalize([text.Count(c => c == 'x'), 1f]);
        });

        var inner = new InMemoryArchiveStore();
        var store = new EmbeddingArchiveStore(inner, embeddings);

        await store.WriteAsync([Row("home", "oslo")], CancellationToken.None);
        var stamped = Assert.Single(inner.All);

        embedded.Clear();
        await store.WriteAsync([stamped], CancellationToken.None);

        Assert.Empty(embedded);
    }

    /// <summary>
    /// The restatement case, end to end: the same address with a new value
    /// carries an inherited vector in, and comes out re-embedded rather than
    /// pointing at what the row used to say.
    /// </summary>
    [Fact]
    public async Task ARestatedRow_IsReEmbedded()
    {
        var inner = new InMemoryArchiveStore();
        var embeddings = Counting();
        var store = new EmbeddingArchiveStore(inner, embeddings);

        await store.WriteAsync([Row("home", "x")], CancellationToken.None);
        var oslo = Assert.Single(inner.All);

        await store.WriteAsync([oslo with { Value = "xxxxxx" }], CancellationToken.None);
        var bergen = inner.All[^1];

        Assert.True(bergen.HasVector(embeddings.ModelId));
        Assert.NotEqual(oslo.Embedding![0], bergen.Embedding![0]);
    }
}
