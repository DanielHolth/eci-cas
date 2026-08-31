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
/// pairs is recovered by listing the directory at construction and decoding
/// the names, so a write never rewrites a companion index file, and the
/// index can never drift from the data (nothing left to rebuild). One file
/// per pair also means Recall's parallel workers touch disjoint files, so
/// the per-file lock below is almost never contended and a Consolidator
/// write only ever blocks readers of the one pair it touches.
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
/// not a data lake. Uses Parquet.Net's high-level ParquetSerializer (POCO
/// row classes) rather than the raw row-group API.
/// </summary>
public sealed class ParquetArchiveStore : IArchiveStore
{
    private const char Separator = '~';

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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexLock = new();
    private readonly HashSet<ArchivePair> _index;

    public ParquetArchiveStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _index = new HashSet<ArchivePair>(PairsIn(_directory), PairComparer.Instance);
    }

    public IReadOnlyList<ArchivePair> Index
    {
        get { lock (_indexLock) { return [.. _index]; } }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, CancellationToken cancellationToken)
    {
        var path = PairPath(pair);
        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<ArchiveRecord> records;
        try
        {
            records = await ReadRecordsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return Ordered(records);
    }

    public async Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        // Grouped by pair and written in parallel: two facts landing in
        // different pairs have no reason to queue behind each other, and a
        // reader of a third pair has no reason to wait for either.
        var writes = records
            .GroupBy(r => r.Pair, PairComparer.Instance)
            .Select(group => AppendAsync(group.Key, [.. group], cancellationToken));
        await Task.WhenAll(writes).ConfigureAwait(false);

        lock (_indexLock)
        {
            foreach (var record in records)
            {
                _index.Add(record.Pair);
            }
        }
    }

    private async Task AppendAsync(ArchivePair pair, List<ArchiveRecord> newRecords, CancellationToken cancellationToken)
    {
        var path = PairPath(pair);
        var gate = LockFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadRecordsAsync(path, cancellationToken).ConfigureAwait(false);
            existing.AddRange(newRecords);
            await WriteRecordsAsync(path, existing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim LockFor(string path) => _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    private string PairPath(ArchivePair pair) => PairPathFor(_directory, pair);

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
