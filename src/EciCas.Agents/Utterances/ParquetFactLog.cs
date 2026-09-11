using System.Globalization;
using System.Text.Json;
using Parquet.Serialization;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// The index, on disk: one parquet per month under <c>facts/</c>, named
/// <c>yyyy-MM.parquet</c>, sharded by the timestamp of the utterance the fact
/// was read out of.
///
/// **Nothing routes on a shard.** Every read is a cosine sweep over the whole
/// corpus, so a shard is a size bound and a backup unit, not a decision --
/// the roadmap's "Time shards, not importance tiers". What sharding buys is
/// that an append rewrites this month rather than a decade.
///
/// **The whole corpus is cached in memory.** Every query sweeps all of it, so
/// a partial cache would just be a read amplifier. At a hundred thousand rows
/// this is forty megabytes of float and a few milliseconds of dot product;
/// the day that stops being true is the day an ANN index earns its keep, and
/// it can be built from the same cache without touching the format.
///
/// **Writes are whole-shard rewrites through a temp file.** Parquet has no
/// append, and a torn write here loses a month of *index* -- which is the
/// point of the split. What was actually said is in <c>utterances/</c>, was
/// written before any of this ran, and is never touched by this class.
/// </summary>
public sealed class ParquetFactLog : IFactLog
{
    public const string DirectoryName = "facts";

    /// <summary>
    /// Flat, and every column a scalar. Not for the archaeologist's sake --
    /// that job belongs to ground truth now -- but because a flat row is what
    /// a rebuild writes fastest and what a bench script can read without
    /// knowing anything about us.
    /// </summary>
    private sealed class Row
    {
        public string? Id { get; set; }
        public string? SourceId { get; set; }
        public string? Text { get; set; }
        public string? Timestamp { get; set; }
        public string? Speaker { get; set; }
        public string? ProfileId { get; set; }
        public string? Keywords { get; set; }
        public string? Embedding { get; set; }
        public string? EmbeddingModelId { get; set; }
        public string? ThreadId { get; set; }
        public string? SupersededBy { get; set; }
        public int? HitCount { get; set; }
        public long? FirstSeenTurn { get; set; }
    }

    private readonly string _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Fact>? _cache;

    public ParquetFactLog(string archiveDirectory)
    {
        _directory = Path.Combine(archiveDirectory, DirectoryName);
        Directory.CreateDirectory(_directory);
    }

    public async Task<IReadOnlyList<Fact>> AllAsync(CancellationToken cancellationToken)
    {
        var warm = _cache;
        if (warm is not null)
        {
            return warm;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task AppendAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken) =>
        facts.Count == 0 ? Task.CompletedTask : MutateAsync(rows => rows.AddRange(facts), cancellationToken);

    public Task UpdateDerivedAsync(IReadOnlyList<FactDerived> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return Task.CompletedTask;
        }

        var byId = updates.GroupBy(u => u.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        return MutateAsync(rows =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (!byId.TryGetValue(rows[i].Id, out var update))
                {
                    continue;
                }

                rows[i] = rows[i] with
                {
                    Embedding = update.Embedding ?? rows[i].Embedding,
                    EmbeddingModelId = update.EmbeddingModelId ?? rows[i].EmbeddingModelId,
                    ThreadId = update.ThreadId ?? rows[i].ThreadId,

                    // Null means "no opinion", which is what almost every
                    // caller has. Unsetting a supersession is a deliberate
                    // empty string, so that "this row is current again" stays
                    // expressible without being indistinguishable from
                    // silence.
                    SupersededBy = update.SupersededBy is null
                        ? rows[i].SupersededBy
                        : update.SupersededBy.Length == 0 ? null : update.SupersededBy,
                };
            }
        }, cancellationToken);
    }

    public Task RecordHitsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        return MutateAsync(rows =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (wanted.Contains(rows[i].Id))
                {
                    rows[i] = rows[i] with { HitCount = rows[i].HitCount + 1 };
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes every shard and empties the cache. Safe in a way no other
    /// store here is safe: there is nothing in this directory that was not
    /// computed from something else.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.parquet"))
            {
                File.Delete(path);
            }

            _cache = [];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Every write goes through here: load, copy, mutate, rewrite the shards
    /// whose contents changed, swap the cache.
    ///
    /// The copy is not defensive habit. <see cref="AllAsync"/> hands the warm
    /// list back directly, so a read sweeping it while a write edits it in
    /// place sees the corpus change mid-cosine; and a write that throws
    /// halfway would otherwise leave rows searchable that nothing persisted.
    /// </summary>
    private async Task MutateAsync(Action<List<Fact>> mutate, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<Fact>(before);
            mutate(rows);
            rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            foreach (var shard in Changed(before, rows))
            {
                await WriteShardAsync(shard, [.. rows.Where(r => Shard(r) == shard)], cancellationToken).ConfigureAwait(false);
            }

            _cache = rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Which shards differ between two versions of the corpus, by content.
    ///
    /// By content rather than by row count, because the writes that matter
    /// most here do not change a shard's membership at all: a thread id or a
    /// hit count lands on a row already in the file, and a diff on keys would
    /// see nothing and persist nothing.
    /// </summary>
    private static HashSet<string> Changed(IReadOnlyList<Fact> before, IReadOnlyList<Fact> after)
    {
        // Not ToDictionary: a corpus that somehow picked up two rows sharing
        // an Id must not turn every future write into a boot-time crash.
        // Last-wins here, so it converges with how UpdateDerivedAsync already
        // resolves the same collision.
        var old = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var row in before)
        {
            old[row.Id] = row;
        }
        var dirty = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in after)
        {
            if (!old.Remove(row.Id, out var previous) || previous != row)
            {
                dirty.Add(Shard(row));
                if (previous is not null)
                {
                    dirty.Add(Shard(previous));
                }
            }
        }

        // Whatever is left in `old` was removed -- a re-extraction that
        // produced fewer rows, or a clear. The shard that lost it still has
        // to be rewritten, or deleted, rather than left behind as a file that
        // disagrees with the cache.
        foreach (var gone in old.Values)
        {
            dirty.Add(Shard(gone));
        }

        return dirty;
    }

    private async Task<IReadOnlyList<Fact>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var rows = new List<Fact>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.parquet").OrderBy(p => p, StringComparer.Ordinal))
        {
            var shard = await ParquetSerializer.DeserializeAsync<Row>(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            rows.AddRange(shard.Data.Select(FromRow));
        }

        rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        _cache = rows;
        return rows;
    }

    private async Task WriteShardAsync(string shard, List<Fact> rows, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, shard + ".parquet");
        if (rows.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await ParquetSerializer.SerializeAsync(rows.Select(ToRow).ToList(), stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>UTC, so a shard boundary does not move with a traveller.</summary>
    private static string Shard(Fact f) =>
        f.Timestamp.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static Row ToRow(Fact f) => new()
    {
        Id = f.Id,
        SourceId = f.SourceId,
        Text = f.Text,
        Timestamp = f.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        Speaker = f.Speaker,
        ProfileId = f.ProfileId,
        Keywords = JsonSerializer.Serialize(f.Keywords),
        Embedding = f.Embedding is null ? null : VectorMath.Encode(f.Embedding),
        EmbeddingModelId = f.EmbeddingModelId,
        ThreadId = f.ThreadId,
        SupersededBy = f.SupersededBy,
        HitCount = f.HitCount,
        FirstSeenTurn = f.FirstSeenTurn,
    };

    private static Fact FromRow(Row r) => new(
        ParquetColumn.Required(r.Id, nameof(r.Id)),
        ParquetColumn.Required(r.SourceId, nameof(r.SourceId)),
        ParquetColumn.Required(r.Text, nameof(r.Text)),
        ParquetColumn.RequiredTime(r.Timestamp, nameof(r.Timestamp)),
        ParquetColumn.Required(r.Speaker, nameof(r.Speaker)),
        r.ProfileId,
        JsonSerializer.Deserialize<List<string>>(r.Keywords ?? "[]") ?? [],
        string.IsNullOrEmpty(r.Embedding) ? null : VectorMath.Decode(r.Embedding),
        r.EmbeddingModelId ?? "",
        r.ThreadId,
        r.SupersededBy,
        r.HitCount ?? 0,
        r.FirstSeenTurn ?? 0);
}
