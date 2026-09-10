using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace EciCas.Substrates;

using EciCas.Core;

/// <summary>
/// Local sentence-transformer over ONNX Runtime: tokenize, one forward pass,
/// mean pool, L2 normalize. Ships multilingual-e5-small, whose tokenizer is
/// XLM-R SentencePiece; a vocab.txt still loads the BERT WordPiece family. CPU only and deliberately so — this runs on the device the
/// persona lives on, next to a minimal-tier local LLM, not on a GPU host.
///
/// Missing weights are announced once and then simply mean Available is
/// false. Throwing at construction would take the whole host down over a
/// file that is optional by design, and throwing per call would turn one
/// missing download into a warning per turn.
///
/// InferenceSession.Run is thread-safe, so the semaphore is not correctness
/// — it is a choice. This runs on the same CPU as a local LLM, and letting
/// several embeds spin up the session's own thread pool at once takes cores
/// away from the thing the person is waiting on. A pass is single-digit
/// milliseconds at this model size, so queueing costs nothing worth having.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger _logger;
    private readonly InferenceSession? _session;
    private readonly Func<string, long[]>? _tokenize;
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
        _tokenize = vocabPath.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
            ? XlmRoberta(vocabPath)
            : Bert(vocabPath);
    }

    public bool Available => _session is not null && _tokenize is not null;

    private static Func<string, long[]> Bert(string vocabPath)
    {
        var tokenizer = BertTokenizer.Create(vocabPath);
        return text => [.. tokenizer.EncodeToIds(text, addSpecialTokens: true).Select(i => (long)i)];
    }

    /// <summary>
    /// XLM-R ids are SentencePiece ids shifted by one: fairseq put
    /// &lt;s&gt;=0 &lt;pad&gt;=1 &lt;/s&gt;=2 &lt;unk&gt;=3 in front of the piece table,
    /// so every piece moves up one and unknown lands on 3. Checked
    /// id-for-id against the HF tokenizer.json, Norwegian included.
    /// </summary>
    private static Func<string, long[]> XlmRoberta(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        var tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        return text =>
        [
            0L,
            .. tokenizer.EncodeToIds(text, considerPreTokenization: false, considerNormalization: true)
                .Select(i => i == 0 ? 3L : i + 1L),
            2L,
        ];
    }

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
        var ids = _tokenize!(text).Take(_options.MaxTokens).ToArray();
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
    ///
    /// The working directory is walked too, because a relative path typed at
    /// a prompt means what it means in the shell that typed it. A build whose
    /// output lives somewhere else entirely -- an artifacts path, a temp
    /// directory -- shares no ancestor with the repo, so the binary's own
    /// chain never reaches the weights however far up it climbs.
    /// </summary>
    private static string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, path);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Nothing found: hand back the binary-relative reading, so the
        // warning names the path an operator would expect to see.
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    public void Dispose() => _session?.Dispose();
}
