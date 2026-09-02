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
    /// Categories that stay in the shared tier however personal the turn
    /// was. Defaults to the two the persona itself owns: "system" is its
    /// identity, "self" is what Reflection thinks — neither belongs to any
    /// one person on a shared device.
    /// </summary>
    public static readonly string[] DefaultSharedCategories = ["system", "self"];

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
    private static List<ArchiveRecord> Merged(IReadOnlyList<ArchiveRecord> existing, IReadOnlyList<ArchiveRecord> incoming)
    {
        var merged = new List<ArchiveRecord>(existing);
        var positions = new Dictionary<(string, string, string), int>();
        for (var i = 0; i < merged.Count; i++)
        {
            positions[RowKey(merged[i])] = i;
        }

        foreach (var record in incoming)
        {
            var key = RowKey(record);
            if (positions.TryGetValue(key, out var at))
            {
                merged[at] = record;
            }
            else
            {
                positions[key] = merged.Count;
                merged.Add(record);
            }
        }

        return merged;
    }

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
            DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture), r.Domain, r.Importance))];
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
        });
        await ParquetSerializer.SerializeAsync(rows, path, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
