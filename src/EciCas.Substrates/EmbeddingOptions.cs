namespace EciCas.Substrates;

/// <summary>
/// Which embedder backs the passage corpus. Local ONNX is the default so the
/// minimal tier keeps working with no key and no connectivity — the same
/// reason every Budget:Tiers entry defaults to "mock".
///
/// The model itself is not committed: a sentence-transformer is ~90MB of
/// weights that git would carry forever and diff badly. ModelPath/VocabPath
/// point at files the operator downloads once (see docs/architecture.md), and
/// their absence is a normal, announced state rather than a startup failure.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>"onnx" (local, default), "openai" (an OpenAI-compatible /embeddings endpoint), or "none".</summary>
    public string Provider { get; set; } = "onnx";

    public string ModelPath { get; set; } = "models/embedding/model.onnx";

    public string VocabPath { get; set; } = "models/embedding/vocab.txt";

    /// <summary>Longer inputs are truncated. Passages are 5-15 words, so this only ever bites on a turn's own text.</summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>Substrates:Providers key to borrow BaseUrl and the API-key environment variable from, when Provider is "openai".</summary>
    public string ApiProvider { get; set; } = "openai";

    public string ApiModel { get; set; } = "text-embedding-3-small";
}
