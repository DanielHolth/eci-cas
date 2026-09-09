using System.Globalization;
using System.Text.Json;
using Parquet.Serialization;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// The log, on disk: one parquet per month under <c>utterances/</c>, named
/// <c>yyyy-MM.parquet</c>.
///
/// **Nothing routes on a shard.** Every read is a cosine sweep over the whole
/// corpus, so a shard is a size bound and a backup unit, not a decision --
/// the roadmap's "Time shards, not importance tiers", and the reason there is
/// no shard-selection method on <see cref="IUtteranceLog"/> to be tempted by.
/// What sharding buys is that an append rewrites this month rather than a
/// decade, and that 2019 can be uploaded once and never again.
///
/// **The whole corpus is cached in memory.** Every query sweeps all of it, so
/// a partial cache would just be a read amplifier. At a hundred thousand rows
/// this is forty megabytes of float and a few milliseconds of dot product;
/// the day that stops being true is the day an ANN index earns its keep, and
/// it can be built from the same cache without touching the format.
///
/// **Writes are whole-shard rewrites through a temp file.** Parquet has no
/// append, and a torn write here loses a month rather than a row, which is
/// the same reasoning ParquetPassageStore gives for its single file.
/// </summary>
public sealed class ParquetUtteranceLog : IUtteranceLog
{
    public const string DirectoryName = "utterances";
    public const string TurnCountFileName = "turns.txt";

    /// <summary>
    /// Flat by design. Ground truth has to be legible to a parquet reader in
    /// a century with none of our code, so the columns a descendant needs --
    /// text, when, who -- are plain scalars, and the derived ones are
    /// nullable so a row written before a pass existed still deserializes
    /// into the honest answer, which is that nobody has judged it yet.
    /// </summary>
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string Speaker { get; set; } = "";
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
    private List<Utterance>? _cache;
    private long _turnsRecorded;

    public ParquetUtteranceLog(string archiveDirectory)
    {
        _directory = Path.Combine(archiveDirectory, DirectoryName);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, TurnCountFileName);
        _turnsRecorded = File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var t) ? t : 0;
    }

    public long TurnsRecorded => Interlocked.Read(ref _turnsRecorded);

    public async Task<IReadOnlyList<Utterance>> AllAsync(CancellationToken cancellationToken)
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

    public Task AppendAsync(IReadOnlyList<Utterance> utterances, CancellationToken cancellationToken) =>
        utterances.Count == 0 ? Task.CompletedTask : MutateAsync(rows => rows.AddRange(utterances), cancellationToken);

    public Task UpdateDerivedAsync(IReadOnlyList<UtteranceDerived> updates, CancellationToken cancellationToken)
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
                    // empty string, because the consolidator's open question
                    // is whether it may say *neither* -- and if it ever may,
                    // that verdict has to be expressible without being
                    // indistinguishable from silence.
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
    /// The denominator advances on every turn whether or not anything was
    /// recalled: a persona asked a hundred questions that needed no memory
    /// has learned something real about the two rows that did get used.
    ///
    /// A failed write costs one turn of one rate and nothing else, so it is
    /// swallowed rather than allowed to fail a turn that has already been
    /// answered.
    /// </summary>
    public async Task RecordTurnAsync(CancellationToken cancellationToken)
    {
        var turns = Interlocked.Increment(ref _turnsRecorded);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, TurnCountFileName),
                turns.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
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
    private async Task MutateAsync(Action<List<Utterance>> mutate, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<Utterance>(before);
            mutate(rows);
            rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // Only the shards that actually changed. A derived-column pass
            // over a decade would otherwise rewrite a decade every time the
            // backfill touched one old row.
            var dirty = Changed(before, rows);
            foreach (var shard in dirty)
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
    private static HashSet<string> Changed(IReadOnlyList<Utterance> before, IReadOnlyList<Utterance> after)
    {
        var old = before.ToDictionary(r => r.Id, StringComparer.Ordinal);
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

        // Whatever is left in `old` was removed. Nothing does that today --
        // the log is append-only by contract -- but a shard that lost its
        // last row still has to be rewritten, or deleted, rather than left
        // behind as a file that disagrees with the cache.
        foreach (var gone in old.Values)
        {
            dirty.Add(Shard(gone));
        }

        return dirty;
    }

    private async Task<IReadOnlyList<Utterance>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var rows = new List<Utterance>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.parquet").OrderBy(p => p, StringComparer.Ordinal))
        {
            var shard = await ParquetSerializer.DeserializeAsync<Row>(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            rows.AddRange(shard.Data.Select(FromRow));
        }

        rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        _cache = rows;
        return rows;
    }

    private async Task WriteShardAsync(string shard, List<Utterance> rows, CancellationToken cancellationToken)
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
    private static string Shard(Utterance u) =>
        u.Timestamp.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static Row ToRow(Utterance u) => new()
    {
        Id = u.Id,
        Text = u.Text,
        Timestamp = u.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        Speaker = u.Speaker,
        ProfileId = u.ProfileId,
        Keywords = JsonSerializer.Serialize(u.Keywords),
        Embedding = u.Embedding is null ? null : VectorMath.Encode(u.Embedding),
        EmbeddingModelId = u.EmbeddingModelId,
        ThreadId = u.ThreadId,
        SupersededBy = u.SupersededBy,
        HitCount = u.HitCount,
        FirstSeenTurn = u.FirstSeenTurn,
    };

    private static Utterance FromRow(Row r) => new(
        r.Id,
        r.Text,
        DateTimeOffset.TryParse(r.Timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var when) ? when : DateTimeOffset.MinValue,
        r.Speaker,
        r.ProfileId,
        JsonSerializer.Deserialize<List<string>>(r.Keywords ?? "[]") ?? [],
        string.IsNullOrEmpty(r.Embedding) ? null : VectorMath.Decode(r.Embedding),
        r.EmbeddingModelId ?? "",
        r.ThreadId,
        r.SupersededBy,
        r.HitCount ?? 0,
        r.FirstSeenTurn ?? 0);
}
