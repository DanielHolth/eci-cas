namespace EciCas.Substrates;

using EciCas.Core;

/// <summary>
/// The configured-off embedder. Reports unavailable rather than throwing, so
/// "Embedding:Provider": "none" and "the ONNX weights aren't downloaded yet"
/// take the identical branch in every caller: no vector search this turn, and
/// the pre-vector Librarian path runs unchanged.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool Available => false;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float[]>>([]);
}
