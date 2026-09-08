using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace EciCas.Substrates;

using EciCas.Core;

/// <summary>
/// Local sentence-transformer (bge-small / all-MiniLM class) over ONNX
/// Runtime: WordPiece tokenize, one forward pass, attention-masked mean pool,
/// L2 normalize. CPU only and deliberately so — this runs on the device the
/// persona lives on, next to a minimal-tier local LLM, not on a GPU host.
///
/// Missing weights are announced once and then simply mean Available is
/// false. Throwing at construction would take the whole host down over a
/// file that is optional by design, and throwing per call would turn one
/// missing download into a warning per turn.
///
/// The session is not thread-safe for concurrent Run on all execution
/// providers, and a turn embeds one short string, so calls are serialized on
/// a semaphore rather than raced — the pass is single-digit milliseconds at
/// this model size.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger _logger;
    private readonly InferenceSession? _session;
    private readonly BertTokenizer? _tokenizer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OnnxEmbeddingProvider(IOptions<EmbeddingOptions> options, ILogger<OnnxEmbeddingProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        var modelPath = Resolve(_options.ModelPath);
        var vocabPath = Resolve(_options.VocabPath);
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            _logger.LogWarning(
                "No embedding model at {ModelPath} — passage retrieval is off until it is downloaded; the persona still recalls facts the pre-vector way",
                modelPath);
            return;
        }

        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath);
    }

    public bool Available => _session is not null && _tokenizer is not null;

    /// <summary>The weights file's own path: two operators pointing at
    /// different downloads are running different models, whatever either
    /// file happens to be called.</summary>
    public string ModelId => Available ? $"onnx:{_options.ModelPath}" : string.Empty;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken)
    {
        if (!Available || texts.Count == 0)
        {
            return [];
        }

        var prefix = PrefixFor(kind);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. texts.Select(t => Embed(prefix + t))];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The e5 family was trained with "query: " and "passage: " on the front
    /// and loses several points without them: the two roles occupy different
    /// regions of its space by design, so a question embedded as a passage is
    /// being compared across that gap. Detected from the weights path rather
    /// than configured, and a no-op for every model that wants no prefix -
    /// which keeps the call identical for every caller whatever is installed.
    /// </summary>
    private string PrefixFor(EmbeddingKind kind) =>
        _options.ModelPath.Contains("e5", StringComparison.OrdinalIgnoreCase)
            ? kind == EmbeddingKind.Query ? "query: " : "passage: "
            : string.Empty;

    private float[] Embed(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text, addSpecialTokens: true).Take(_options.MaxTokens).Select(i => (long)i).ToArray();
        var shape = new[] { 1, ids.Length };
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
        };

        // Not every export declares token_type_ids; passing an input the graph
        // doesn't have is an error, so it is added only when the model asks.
        if (_session!.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[ids.Length], shape)));
        }

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First().AsTensor<float>();
        var width = hidden.Dimensions[^1];

        // Mean over tokens. Every token is real here — a single un-padded
        // sequence per call — so the attention mask is all ones and the pool
        // is a plain average rather than a masked one.
        var pooled = new float[width];
        for (var t = 0; t < ids.Length; t++)
        {
            for (var d = 0; d < width; d++)
            {
                pooled[d] += hidden[0, t, d];
            }
        }

        for (var d = 0; d < width; d++)
        {
            pooled[d] /= ids.Length;
        }

        return VectorMath.Normalize(pooled);
    }

    /// <summary>
    /// A relative weights path, resolved against the binary and then against
    /// each directory above it until the file turns up.
    ///
    /// Joining to AppContext.BaseDirectory alone was wrong in the ordinary
    /// case: the weights live at the repo root, nothing copies 90MB of them
    /// into bin/Debug on every build, and nobody wants a build that does.
    /// So the path pointed at a file that had never existed, the provider
    /// warned once, and every vector path in the system went quietly off --
    /// pair sweeps, row narrowing and Hindsight's wake alike -- while the
    /// weights sat four directories up.
    ///
    /// Walking up costs a few File.Exists calls once at startup and makes
    /// the same configured path work from `dotnet run`, from bin, and from
    /// a published layout, where the weights sit beside the binary and the
    /// first probe hits.
    /// </summary>
    private static string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Nothing found: hand back the binary-relative reading, so the
        // warning names the path an operator would expect to see.
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    public void Dispose() => _session?.Dispose();
}
