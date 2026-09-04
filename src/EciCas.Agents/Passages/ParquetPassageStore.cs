using System.Globalization;
using System.Text.Json;
using Parquet.Serialization;

namespace EciCas.Agents.Passages;

using EciCas.Core;

/// <summary>
/// Parquet-backed IPassageStore: one file, passages.parquet, in the archive
/// root beside the pair files. One file rather than one-per-something,
/// because unlike the fact archive there is no address to shard on — every
/// query is a cosine sweep over the whole corpus, so any split would just be
/// N reads to rebuild the same list.
///
/// The corpus is small by construction: one passage per Reflection batch,
/// and the revisit replaces rather than appends. At the shipped BatchSize
/// of 5 that is a row per five concluded turns, so brute-force cosine over
/// the in-memory cache is microseconds and needs no ANN index — the same
/// "a persona's own knowledge base, not a data lake" reasoning
/// ParquetArchiveStore gives for rewriting a whole pair file per write.
///
/// The embedding rides as a base64 blob rather than a Parquet list column:
/// it is opaque to every reader (nothing filters or projects on a single
/// dimension), and a fixed-width scalar column keeps the row schema flat
/// enough that ParquetSerializer's POCO path handles it unchanged. Pairs go
/// as JSON for the same reason — a category or topic is LLM-written free
/// text, and JSON is unambiguous about a separator appearing inside one.
/// </summary>
public sealed class ParquetPassageStore : IPassageStore
{
    public const string FileName = "passages.parquet";

    private sealed class PassageRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Pairs { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string Embedding { get; set; } = "";

        // Nullable so a file written before lineage existed still
        // deserializes: Parquet gives a missing column its default, and for
        // these three that default is the honest answer — nobody recorded an
        // ancestry, so we do not claim one.
        public string? ParentIds { get; set; }
        public int? EchoDepth { get; set; }
        public int? Generation { get; set; }

        // Same nullability, different meaning: null here is "written before
        // the stamp existed", which the mismatch check must not read as a
        // model that disagrees with the current one.
        public string? ModelId { get; set; }
    }

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Passage>? _cache;

    public ParquetPassageStore(string directory) => _path = Path.Combine(directory, FileName);

    public async Task<IReadOnlyList<PassageHit>> SearchAsync(float[] query, int topK, double minScore, CancellationToken cancellationToken)
    {
        if (topK <= 0)
        {
            return [];
        }

        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. all
            .Select(p => new PassageHit(p, VectorMath.Cosine(query, p.Embedding)))
            .Where(h => h.Score >= minScore)
            .OrderByDescending(h => h.Score)
            .Take(topK)];
    }

    public async Task<Passage?> LatestAsync(CancellationToken cancellationToken)
    {
        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return all.Count == 0 ? null : all.MaxBy(p => p.Timestamp);
    }

    public async Task<IReadOnlyCollection<string>> StampedModelsAsync(CancellationToken cancellationToken)
    {
        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Select(p => p.ModelId).Where(m => m.Length > 0).Distinct()];
    }

    public async Task WriteAsync(IReadOnlyList<Passage> added, string? replacedId, CancellationToken cancellationToken)
    {
        if (added.Count == 0 && replacedId is null)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A copy, never the cache itself: ReadUnlockedAsync hands back
            // _cache directly when warm, and LoadAsync's fast path returns it
            // without taking this lock. Editing it in place lets a Hindsight
            // or Librarian search enumerating the same list see it change
            // mid-sweep. It also keeps the cache honest if the write below
            // throws — rows nothing persisted would otherwise stay searchable
            // until restart. ParquetArchiveStore.Merged already works this
            // way; this is the same shape.
            var rows = new List<Passage>(await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false));
            if (replacedId is not null)
            {
                rows.RemoveAll(p => p.Id == replacedId);
            }

            rows.AddRange(added);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Through a temp file: the corpus is one file, so a crash or a
            // full disk partway through an in-place write loses every note
            // the persona ever wrote, not the one being added.
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await ParquetSerializer.SerializeAsync(rows.Select(ToRow).ToList(), stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
            _cache = rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<Passage>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cache ??= await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<Passage>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        if (!File.Exists(_path))
        {
            return [];
        }

        var result = await ParquetSerializer.DeserializeAsync<PassageRow>(_path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. result.Data.Select(FromRow)];
    }

    private static PassageRow ToRow(Passage p) => new()
    {
        Id = p.Id,
        Text = p.Text,
        Pairs = JsonSerializer.Serialize(p.Pairs),
        Timestamp = p.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        Embedding = Convert.ToBase64String(EncodeFloats(p.Embedding)),
        ParentIds = JsonSerializer.Serialize(p.ParentIds),
        EchoDepth = p.EchoDepth,
        Generation = p.Generation,
        ModelId = p.ModelId,
    };

    private static Passage FromRow(PassageRow r) => new(
        r.Id,
        r.Text,
        JsonSerializer.Deserialize<List<ArchivePair>>(r.Pairs) ?? [],
        // Invariant on both sides, matching ParquetArchiveStore. The fallback
        // is silent, so a culture mismatch would not surface as an error but
        // as every note being two millennia old — wrong ages in Hindsight and
        // LatestAsync picking whichever row parsed.
        DateTimeOffset.TryParse(r.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts) ? ts : DateTimeOffset.MinValue,
        DecodeFloats(Convert.FromBase64String(r.Embedding)),
        r.ParentIds is null ? [] : JsonSerializer.Deserialize<List<string>>(r.ParentIds) ?? [],
        r.EchoDepth ?? 0,
        r.Generation ?? 0,
        r.ModelId ?? "");

    private static byte[] EncodeFloats(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DecodeFloats(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }
}
