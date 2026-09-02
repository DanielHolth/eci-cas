using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>Passage-store double, mirroring ParquetPassageStore's semantics without the file.</summary>
internal sealed class InMemoryPassageStore : IPassageStore
{
    public List<Passage> Passages { get; } = [];

    public Task<IReadOnlyList<PassageHit>> SearchAsync(float[] query, int topK, double minScore, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PassageHit>>([.. Passages
            .Select(p => new PassageHit(p, VectorMath.Cosine(query, p.Embedding)))
            .Where(h => h.Score >= minScore)
            .OrderByDescending(h => h.Score)
            .Take(topK)]);

    public Task<Passage?> LatestAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Passages.Count == 0 ? null : Passages.MaxBy(p => p.Timestamp));

    public Task WriteAsync(IReadOnlyList<Passage> added, string? replacedId, CancellationToken cancellationToken)
    {
        if (replacedId is not null)
        {
            Passages.RemoveAll(p => p.Id == replacedId);
        }

        Passages.AddRange(added);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Embedder double: a fixed vector per text, so a test can make two strings
/// match or not match without a model. Unavailable by default — most agent
/// tests predate vectors and must keep exercising the pre-vector path.
/// </summary>
internal sealed class StubEmbeddings(Func<string, float[]>? embed = null) : IEmbeddingProvider
{
    public bool Available => embed is not null;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float[]>>(embed is null ? [] : [.. texts.Select(embed)]);
}
