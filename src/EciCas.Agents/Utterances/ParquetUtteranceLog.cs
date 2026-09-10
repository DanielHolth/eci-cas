using System.Globalization;
using Parquet.Serialization;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Ground truth, on disk: one parquet per month under <c>utterances/</c>,
/// named <c>yyyy-MM.parquet</c>, plus the turn counter beside them.
///
/// **Append-only, and there is no other verb.** No derived-column write, no
/// hit counter, no supersession. Those all moved to <see cref="ParquetFactLog"/>
/// when the archive split in two, and the absence of them here is the point:
/// the file holding what was actually said is now only ever added to, so no
/// bug in a whole-shard rewrite can eat it.
///
/// **A shard is a size bound, not a decision.** Nothing routes on it -- the
/// only reader is the backfill, and it reads all of them. What sharding buys
/// is that an append rewrites this month rather than a decade, and that 2019
/// can be uploaded once and never again.
///
/// **Small enough to keep.** Without the vector a row is a sentence and four
/// short strings. A decade of conversation is megabytes, which is what makes
/// "never delete anything anybody said" an affordable promise rather than a
/// slogan.
/// </summary>
public sealed class ParquetUtteranceLog : IUtteranceLog
{
    public const string DirectoryName = "utterances";
    public const string TurnCountFileName = "turns.txt";

    /// <summary>
    /// Flat by design, and every column a plain scalar. This is the row a
    /// descendant reads in a century with a parquet reader and none of our
    /// code, so nothing on it is encoded, packed, or indirected.
    /// </summary>
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string? ProfileId { get; set; }
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

    /// <summary>
    /// Load, add, rewrite only the shards the new rows landed in, swap the
    /// cache. Since nothing is ever modified in place, the dirty set is just
    /// the months the arrivals belong to -- no content diff needed.
    /// </summary>
    public async Task AppendAsync(IReadOnlyList<Utterance> utterances, CancellationToken cancellationToken)
    {
        if (utterances.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            // The copy is not defensive habit: AllAsync hands the warm list
            // back directly, and a reader walking it while this appends in
            // place would see the corpus change underneath it.
            var rows = new List<Utterance>(before);
            rows.AddRange(utterances);
            rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            foreach (var shard in utterances.Select(Shard).ToHashSet(StringComparer.Ordinal))
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

    /// <summary>
    /// Through a temp file and a move: parquet has no append, so a shard is
    /// rewritten whole, and a torn write here would lose a month of what was
    /// said -- the one thing in the system that cannot be recomputed.
    /// </summary>
    private async Task WriteShardAsync(string shard, List<Utterance> rows, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, shard + ".parquet");
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
    };

    private static Utterance FromRow(Row r) => new(
        r.Id,
        r.Text,
        DateTimeOffset.TryParse(r.Timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var when) ? when : DateTimeOffset.MinValue,
        r.Speaker,
        r.ProfileId);
}
