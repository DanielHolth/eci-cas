using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Parquet.Serialization;

namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// Parquet-backed IArchiveStore: one file per (Category, Topic) pair, named
/// {esc(category)}~{esc(topic)}.parquet and created lazily on first write.
///
/// The file name *is* the index. There is no index.parquet: the set of
/// pairs is recovered by listing the directory and decoding the names, so a
/// write never rewrites a companion index file, and the index can never
/// drift from the data (nothing left to rebuild). One file per pair also
/// means Recall's parallel workers touch disjoint files, so the per-file
/// lock below is almost never contended and a Archivist write only ever
/// blocks readers of the one pair it touches.
///
/// Personal knowledge is scoped by *directory*, not by a filename or a new
/// column: the archive root holds shared pairs, and profiles/{id}/ holds one
/// person's own, under exactly the same naming convention. So "the name is
/// the index" holds inside each directory unchanged, and today's flat
/// archive simply becomes the shared tier — no schema change, no migration,
/// no rewrite of existing files. Reads union the two tiers with the profile
/// winning; writes land in the profile's directory unless the fact's
/// category is one the operator declared shared.
///
/// Names are percent-escaped down to [A-Za-z0-9._-] so an LLM-written topic
/// containing a slash, colon or space can't produce an illegal or ambiguous
/// path; '~' itself is escaped inside each half, which is what makes the
/// single-character separator unambiguous. The escaping is reversible, since
/// decoding it is how the index is read back.
///
/// Parquet has no in-place append: each write reads the target pair file (if
/// any), appends the new rows in memory, and rewrites the whole file as a
/// single row group. Fine at this scale — a persona's own knowledge base,
/// not a data lake. Since a write already holds the whole pair in memory it
/// keeps it, and reads cache what they load, so a hot pair costs one file
/// read for the process's lifetime rather than one per turn.
///
/// Uses Parquet.Net's high-level ParquetSerializer (POCO row classes)
/// rather than the raw row-group API.
/// </summary>
public sealed class ParquetArchiveStore : IArchiveStore
{
    private const char Separator = '~';

    public const string ProfilesDirectoryName = "profiles";

    /// <summary>
    /// The recency lane, one file per directory beside the pair files. The
    /// name has no separator in it, so TryDecodeName rejects it and it can
    /// never appear in the index or be mistaken for a pair - the same
    /// property that makes the file listing safe to use as the index.
    /// </summary>
    public const string RecentFileName = "recent.parquet";

    /// <summary>Where the recalled-turn denominator lives, beside the shelf it counts for.</summary>
    public const string TurnCountFileName = "turns.txt";

    /// <summary>
    /// How far back the lane reaches. Time, not rows: "lately" is a span,
    /// and a row count makes the lane short in a busy month and long in a
    /// quiet one. A year of an ordinary archive is a rounding error on disk,
    /// and rows falling out of it are not lost - the pair file that owns
    /// them is the long-term store, and this is a derived view of it.
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(365);

    /// <summary>
    /// Categories that stay in the shared tier however personal the turn
    /// was. One: "assistant", everything the persona knows about itself —
    /// its identity, the architecture it runs on, and what Reflection thinks
    /// under assistant/reflection. None of it belongs to any one person on a
    /// shared device. It was two until "self" was folded in; the pair
    /// addressing keeps the files apart without needing a second category.
    ///
    /// Named from AssistantScope rather than spelled here, because this
    /// string and the ones the writing agents use have to be the same
    /// string or a row is filed per-profile by accident.
    /// </summary>
    public static readonly string[] DefaultSharedCategories = [AssistantScope.Name];

    private sealed class RecordRow
    {
        public string Category { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Subtopic { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string Domain { get; set; } = "";
        public double Importance { get; set; }

        // Nullable so a pair file written before the sentence existed still
        // deserializes: Parquet gives a missing column its default, and null
        // is the honest answer — nobody wrote a sentence for that row, which
        // is not the same as writing an empty one. Verified against
        // Parquet.Net 6.1.0 rather than assumed: an old file reads back with
        // this null, and a new file still reads under the old schema.
        public string? Sentence { get; set; }

        // The row vector, base64 over the raw float bytes. A scalar string
        // column rather than a list of floats: nothing here filters or
        // projects on a single dimension, and a flat schema is what keeps
        // ParquetSerializer's POCO path working - the same reason the rest of
        // this class is scalars. Nullable for the same reason Sentence is,
        // and for a stronger one: an unembedded row and a row embedded to
        // nothing have to stay distinguishable, because the rule that decides
        // whether a pair may be swept by cosine is "every row carries one".
        public string? Embedding { get; set; }

        // Which embedder made it, and a hash of the text it was made from.
        // Both are checked before the vector is used: a vector from another
        // model, or one made from words the row no longer says, scores
        // confidently and means nothing.
        public string? EmbeddingModel { get; set; }

        public string? EmbeddingHash { get; set; }

        // How often this row has been recalled, and when it last was. Both
        // nullable for the reason every column added since the first release
        // is: a pair file written before they existed reads back with them
        // null, which is "nobody has counted", not "counted zero".
        public long? Hits { get; set; }

        public string? LastHit { get; set; }
    }

    /// <summary>Pairs are addresses, and addresses are case-insensitive — as are the file names that carry them.</summary>
    private sealed class PairComparer : IEqualityComparer<ArchivePair>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals(ArchivePair? x, ArchivePair? y) =>
            x is null || y is null
                ? ReferenceEquals(x, y)
                : string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Topic, y.Topic, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ArchivePair pair) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Category),
            StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Topic));
    }

    private readonly string _directory;
    private readonly HashSet<string> _sharedCategories;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexLock = new();
    private long _turnsRecorded;
    private readonly Dictionary<string, HashSet<ArchivePair>> _indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last known contents of each pair file, keyed by path. This process is
    /// the only writer, so a cached pair can only go stale by our own hand —
    /// and AppendAsync updates it in the same critical section that writes
    /// the file. Bounded by the archive's own size, which is one persona's
    /// knowledge, not a data lake. Reads under the per-path gate, so the
    /// dictionary is only ever touched by one task per path at a time.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<ArchiveRecord>> _pairs = new(StringComparer.OrdinalIgnoreCase);

    public ParquetArchiveStore(string directory, IEnumerable<string>? sharedCategories = null)
    {
        _directory = directory;
        _sharedCategories = new HashSet<string>(sharedCategories ?? DefaultSharedCategories, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_directory);
        _turnsRecorded = LoadTurnCount(_directory);
    }

    public IReadOnlyList<ArchivePair> IndexFor(string? profileId)
    {
        lock (_indexLock)
        {
            var shared = IndexIn(_directory);
            return profileId is null
                ? [.. shared]
                : [.. shared.Union(IndexIn(ProfileDirectoryFor(_directory, profileId)), PairComparer.Instance)];
        }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, string? profileId, CancellationToken cancellationToken)
    {
        var shared = await ReadPairAsync(_directory, pair, cancellationToken).ConfigureAwait(false);
        if (profileId is null)
        {
            return Ordered(shared);
        }

        var personal = await ReadPairAsync(ProfileDirectoryFor(_directory, profileId), pair, cancellationToken).ConfigureAwait(false);
        if (personal.Count == 0)
        {
            return Ordered(shared);
        }

        // The profile wins on collision: a shared row and a personal one at
        // the same address are the same question answered twice, and the
        // answer belonging to the person asking is the right one.
        var claimed = personal.Select(RowKey).ToHashSet();
        return Ordered(personal.Concat(shared.Where(r => !claimed.Contains(RowKey(r)))));
    }

    public async Task WriteAsync(IReadOnlyList<ArchiveRecord> records, string? profileId, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        // Grouped by directory, then by pair, and written in parallel: two
        // facts landing in different pairs have no reason to queue behind
        // each other, and a reader of a third pair has no reason to wait for
        // either.
        var writes = records
            .GroupBy(r => DirectoryFor(r.Category, profileId), StringComparer.OrdinalIgnoreCase)
            .SelectMany(byDirectory => byDirectory
                .GroupBy(r => r.Pair, PairComparer.Instance)
                .Select(byPair => AppendAsync(byDirectory.Key, byPair.Key, [.. byPair], cancellationToken)));
        await Task.WhenAll(writes).ConfigureAwait(false);

        // The lane is written after the shelf, not with it: a row is in the
        // archive once its pair file holds it, and the lane is a view of
        // that. If this half failed the fact would still be on file and
        // still findable by its address, which is the weaker of the two to
        // lose.
        var lanes = records
            .GroupBy(r => DirectoryFor(r.Category, profileId), StringComparer.OrdinalIgnoreCase)
            .Select(byDirectory => AppendRecentAsync(byDirectory.Key, [.. byDirectory], cancellationToken));
        await Task.WhenAll(lanes).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends to one directory's lane. Same read-merge-rewrite as a pair
    /// file and the same per-path gate, but keyed on the full address:
    /// within a pair the subtopic/subject/key is the whole identity, across
    /// the lane it is not, and two drawers may each hold a cost for a
    /// renewal.
    /// </summary>
    private async Task AppendRecentAsync(string directory, List<ArchiveRecord> newRecords, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, RecentFileName);
        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var merged = Merged(await CachedAsync(path, cancellationToken).ConfigureAwait(false), newRecords, LaneKey);
            await WriteRecordsAsync(path, merged, cancellationToken).ConfigureAwait(false);
            _pairs[path] = merged;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> RecentAsync(string? profileId, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        IEnumerable<ArchiveRecord> rows = await LaneAsync(_directory, cancellationToken).ConfigureAwait(false);
        if (profileId is not null)
        {
            var personal = await LaneAsync(ProfileDirectoryFor(_directory, profileId), cancellationToken).ConfigureAwait(false);

            // The profile wins on collision, as it does for a pair: same
            // address, same question, and the answer belonging to the person
            // asking is the right one.
            var claimed = personal.Select(LaneKey).ToHashSet();
            rows = personal.Concat(rows.Where(r => !claimed.Contains(LaneKey(r))));
        }

        // Trimmed at boot, but filtered here too: a process left running for
        // a year would otherwise keep serving rows the window has passed.
        var cutoff = DateTimeOffset.UtcNow - RecentWindow;
        return [.. rows.Where(r => r.Timestamp >= cutoff).OrderByDescending(r => r.Timestamp).Take(limit)];
    }

    /// <summary>One directory's lane, through the same cache and gate a pair read uses.</summary>
    private async Task<IReadOnlyList<ArchiveRecord>> LaneAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, RecentFileName);
        if (_pairs.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CachedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops everything older than RecentWindow from every lane in the
    /// archive. Called once at startup rather than on every write: the lane
    /// is bounded by a year of one person's facts either way, and trimming
    /// on write would rewrite the whole lane to remove nothing on all but
    /// one turn a day.
    /// </summary>
    public async Task TrimRecentAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - RecentWindow;
        var profiles = Path.Combine(_directory, ProfilesDirectoryName);
        var directories = new List<string> { _directory };
        if (Directory.Exists(profiles))
        {
            directories.AddRange(Directory.EnumerateDirectories(profiles));
        }

        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, RecentFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var gate = LockFor(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rows = await CachedAsync(path, cancellationToken).ConfigureAwait(false);
                var kept = rows.Where(r => r.Timestamp >= cutoff).ToList();
                if (kept.Count == rows.Count)
                {
                    continue;
                }

                await WriteRecordsAsync(path, kept, cancellationToken).ConfigureAwait(false);
                _pairs[path] = kept;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public long TurnsRecorded => Interlocked.Read(ref _turnsRecorded);

    /// <summary>
    /// Credits the rows a turn actually used, and counts the turn.
    ///
    /// The counter is the denominator, so it advances on every turn whether
    /// or not anything was recalled — a persona asked a hundred questions
    /// that needed no fact has learned something real about the two rows
    /// that did get used.
    ///
    /// Rows are credited wherever they live. A recalled row may have come
    /// from the shared tier, from the profile's own, or from the recency
    /// lane's copy of either, and the caller does not know which — it was
    /// handed a union. So each candidate file is opened once and every row
    /// in it whose address was recalled is credited, which also keeps the
    /// lane's copy and the pair's copy from drifting apart.
    /// </summary>
    public async Task RecordRecallAsync(IReadOnlyList<ArchiveRecord> recalled, string? profileId, CancellationToken cancellationToken)
    {
        var turns = Interlocked.Increment(ref _turnsRecorded);
        await SaveTurnCountAsync(turns, cancellationToken).ConfigureAwait(false);

        if (recalled.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var directories = profileId is null
            ? new[] { _directory }
            : [_directory, ProfileDirectoryFor(_directory, profileId)];

        var paths = directories
            .SelectMany(d => recalled
                .Select(r => PairPathFor(d, r.Pair))
                .Append(Path.Combine(d, RecentFileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var hit = recalled.Select(RowKey).ToHashSet();
        await Task.WhenAll(paths.Select(path => CreditAsync(path, hit, now, cancellationToken))).ConfigureAwait(false);
    }

    private async Task CreditAsync(string path, HashSet<(string, string, string)> hit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await CachedAsync(path, cancellationToken).ConfigureAwait(false);
            var credited = rows
                .Select(r => hit.Contains(RowKey(r)) ? r with { Hits = r.Hits + 1, LastHitAt = now } : r)
                .ToList();

            if (!credited.Where((r, i) => !ReferenceEquals(r, rows[i])).Any())
            {
                return;
            }

            await WriteRecordsAsync(path, credited, cancellationToken).ConfigureAwait(false);
            _pairs[path] = credited;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The turn count is one number and it is written every turn, so it gets
    /// a plain text file rather than a row in anything: parquet for a single
    /// integer would cost a schema and a rewrite to say what nine bytes say.
    /// A missing or unreadable file means zero — the archive predates
    /// counting, which is the same starting state as a new one.
    /// </summary>
    private static long LoadTurnCount(string directory)
    {
        var path = Path.Combine(directory, TurnCountFileName);
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var turns) ? turns : 0;
    }

    private async Task SaveTurnCountAsync(long turns, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, TurnCountFileName);
        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(path, turns.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A lost turn count is a slightly wrong denominator on one row's
            // rate, and nothing else. Not worth failing a turn that has
            // already answered the person.
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AppendAsync(string directory, ArchivePair pair, List<ArchiveRecord> newRecords, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = PairPathFor(directory, pair);
        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = Merged(await CachedAsync(path, cancellationToken).ConfigureAwait(false), newRecords);
            await WriteRecordsAsync(path, existing, cancellationToken).ConfigureAwait(false);
            _pairs[path] = existing;
        }
        finally
        {
            gate.Release();
        }

        lock (_indexLock)
        {
            IndexIn(directory).Add(pair);
        }
    }

    /// <summary>
    /// A fact restated is not a second fact. Rows are addressed by
    /// subtopic/subject/key within a pair, so a new row at an address that
    /// already exists replaces it outright: the latest statement is the true
    /// one, and "lives in Oslo" followed by "lives in Bergen" must not leave
    /// both on file for the picking model to choose between. Without this an
    /// archive grows with every restatement and the pair prompt fills with
    /// its own history.
    ///
    /// Position is kept, so an updated fact stays where it was rather than
    /// jumping to the end — reads sort by importance anyway, but a stable
    /// file makes a diff readable.
    /// </summary>
    private static List<ArchiveRecord> Merged(
        IReadOnlyList<ArchiveRecord> existing,
        IReadOnlyList<ArchiveRecord> incoming,
        Func<ArchiveRecord, object>? identity = null)
    {
        var address = identity ?? (r => RowKey(r));
        var merged = new List<ArchiveRecord>(existing);
        var positions = new Dictionary<object, int>();
        for (var i = 0; i < merged.Count; i++)
        {
            positions[address(merged[i])] = i;
        }

        foreach (var record in incoming)
        {
            var key = address(record);
            if (positions.TryGetValue(key, out var at))
            {
                // The new statement wins on every field except the two
                // nobody stated: a restated fact keeps the history of having
                // been asked for. Written as a max rather than an assignment
                // because an incoming row may legitimately carry counts —
                // RecordRecallAsync writes through this same path.
                var previous = merged[at];
                merged[at] = record with
                {
                    Hits = Math.Max(previous.Hits, record.Hits),
                    LastHitAt = Later(previous.LastHitAt, record.LastHitAt),
                };
            }
            else
            {
                positions[key] = merged.Count;
                merged.Add(record);
            }
        }

        return merged;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private async Task<IReadOnlyList<ArchiveRecord>> ReadPairAsync(string directory, ArchivePair pair, CancellationToken cancellationToken)
    {
        var path = PairPathFor(directory, pair);

        // Fast path: no lock at all on a hot pair. Recall reads the same
        // handful of pairs every turn, and a stale read is impossible —
        // only AppendAsync replaces an entry, and it does so having already
        // written the file.
        if (_pairs.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CachedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Cached contents of one pair file, read through on a miss. Callers hold that path's gate.</summary>
    private async Task<IReadOnlyList<ArchiveRecord>> CachedAsync(string path, CancellationToken cancellationToken)
    {
        if (_pairs.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var records = await ReadRecordsAsync(path, cancellationToken).ConfigureAwait(false);
        _pairs[path] = records;
        return records;
    }

    /// <summary>Where a fact belongs: the profile's own tier, unless its category is shared or there is no profile at all.</summary>
    private string DirectoryFor(string category, string? profileId) =>
        profileId is null || _sharedCategories.Contains(category)
            ? _directory
            : ProfileDirectoryFor(_directory, profileId);

    /// <summary>
    /// Forgets everything read so far. For one caller and one moment: the
    /// boot-time backfill rewrites pair files underneath this store, and a
    /// row cached before it ran would stay vectorless in memory for the whole
    /// process while the file on disk was already fixed -- the pair silently
    /// cold until the next restart. Safe only because nothing is serving
    /// turns yet; it is not a general eviction and there is no lock here.
    /// </summary>
    public void Invalidate()
    {
        _pairs.Clear();
        _indexes.Clear();
    }

    /// <summary>Decoded once per directory and kept: the file listing IS the index, so it only has to be read the first time that directory is touched.</summary>
    private HashSet<ArchivePair> IndexIn(string directory)
    {
        if (!_indexes.TryGetValue(directory, out var index))
        {
            index = new HashSet<ArchivePair>(Directory.Exists(directory) ? PairsIn(directory) : [], PairComparer.Instance);
            _indexes[directory] = index;
        }

        return index;
    }

    /// <summary>What makes two rows in one pair the same fact: the rest of the address, everything the pair itself doesn't carry.</summary>
    private static (string Subtopic, string Subject, string Key) RowKey(ArchiveRecord record) =>
        (record.Subtopic.ToLowerInvariant(), record.Subject.ToLowerInvariant(), record.Key.ToLowerInvariant());

    /// <summary>The same, plus the pair - the lane holds every drawer at once, so the pair is part of the address again.</summary>
    private static object LaneKey(ArchiveRecord record) =>
        (record.Category.ToLowerInvariant(), record.Topic.ToLowerInvariant(),
            record.Subtopic.ToLowerInvariant(), record.Subject.ToLowerInvariant(), record.Key.ToLowerInvariant());

    private SemaphoreSlim LockFor(string path) => _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Stable order for a pair's rows: Importance first, then fields that
    /// break ties deterministically, so the same archive always presents the
    /// same sequence to the picking model.
    /// </summary>
    private static IReadOnlyList<ArchiveRecord> Ordered(IEnumerable<ArchiveRecord> records) =>
        [.. records
            .OrderByDescending(r => r.Importance)
            .ThenByDescending(r => r.Timestamp)
            .ThenBy(r => r.Subtopic, StringComparer.Ordinal)
            .ThenBy(r => r.Subject, StringComparer.Ordinal)
            .ThenBy(r => r.Key, StringComparer.Ordinal)];

    /// <summary>Pair file path for a directory, using the same naming convention as instance writes.</summary>
    public static string PairPathFor(string directory, ArchivePair pair) =>
        Path.Combine(directory, $"{Escape(pair.Category)}{Separator}{Escape(pair.Topic)}.parquet");

    /// <summary>
    /// One person's own tier under an archive root. The id is escaped the
    /// way a pair name is: profile ids are slugs the escaping leaves
    /// untouched, so this is a no-op for every legitimate id and a
    /// containment guard for anything else that reaches here.
    /// </summary>
    public static string ProfileDirectoryFor(string archiveDirectory, string profileId) =>
        Path.Combine(archiveDirectory, ProfilesDirectoryName, Escape(profileId));

    /// <summary>Every pair a directory currently holds, decoded from its file names — this is the whole index.</summary>
    public static IReadOnlyList<ArchivePair> PairsIn(string directory)
    {
        var pairs = new List<ArchivePair>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.parquet"))
        {
            if (TryDecodeName(Path.GetFileNameWithoutExtension(path), out var pair))
            {
                pairs.Add(pair);
            }
        }

        return pairs;
    }

    /// <summary>Splits an escaped file name back into its pair. False for any name that isn't one of ours.</summary>
    public static bool TryDecodeName(string fileName, out ArchivePair pair)
    {
        pair = default!;
        var separator = fileName.IndexOf(Separator);
        if (separator < 0)
        {
            return false;
        }

        pair = new ArchivePair(Unescape(fileName[..separator]), Unescape(fileName[(separator + 1)..]));
        return true;
    }

    /// <summary>
    /// Percent-escapes everything outside [A-Za-z0-9._-] over UTF-8 bytes.
    /// Covers the platform's illegal characters, the separator, and anything
    /// that would make a name ambiguous or awkward, in one rule rather than a
    /// per-platform deny-list.
    /// </summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        // A name may not end in '.' on Windows, and '.' is otherwise legal
        // mid-name, so only a trailing one needs escaping.
        if (builder.Length > 0 && builder[^1] == '.')
        {
            builder.Length -= 1;
            builder.Append("%2E");
        }

        return builder.ToString();
    }

    public static string Unescape(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    public static async Task<List<ArchiveRecord>> ReadRecordsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var result = await ParquetSerializer.DeserializeAsync<RecordRow>(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. result.Data.Select(r => new ArchiveRecord(
            r.Category, r.Topic, r.Subtopic, r.Subject, r.Key, r.Value,
            DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture), r.Domain, r.Importance, r.Sentence ?? "",
            string.IsNullOrEmpty(r.Embedding) ? null : VectorMath.Decode(r.Embedding),
            r.EmbeddingModel ?? "", r.EmbeddingHash ?? "", r.Hits ?? 0,
            string.IsNullOrEmpty(r.LastHit) ? null : DateTimeOffset.Parse(r.LastHit, CultureInfo.InvariantCulture)))];
    }

    public static async Task WriteRecordsAsync(string path, List<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        var rows = records.Select(r => new RecordRow
        {
            Category = r.Category,
            Topic = r.Topic,
            Subtopic = r.Subtopic,
            Subject = r.Subject,
            Key = r.Key,
            Value = r.Value,
            Timestamp = r.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            Domain = r.Domain,
            Importance = r.Importance,
            Sentence = r.Sentence,
            Embedding = r.Embedding is { Length: > 0 } v ? VectorMath.Encode(v) : null,
            EmbeddingModel = r.EmbeddingModelId.Length == 0 ? null : r.EmbeddingModelId,
            EmbeddingHash = r.EmbeddingHash.Length == 0 ? null : r.EmbeddingHash,
            Hits = r.Hits == 0 ? null : r.Hits,
            LastHit = r.LastHitAt?.ToString("O", CultureInfo.InvariantCulture),
        });
        // Through a temp file, for the reason JsonlAgentStateStore already
        // gives about the persona's state: a crash, a full disk or a killed
        // process midway leaves the original intact, where writing straight
        // over the destination leaves a truncated file. The archive's stated
        // goal is to outlive this software, so it is the last place that
        // should risk a half-written pair.
        var temp = path + ".tmp";
        await ParquetSerializer.SerializeAsync(rows, temp, cancellationToken: cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
