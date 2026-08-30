using System.Globalization;
using Parquet.Serialization;

namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// Parquet-backed IArchiveStore: one {category}.parquet file per category,
/// created lazily on first write to a new category. A companion
/// index.parquet holds every distinct (Category, Topic, Subtopic) triple
/// seen so far, hydrated once at construction and kept as an in-memory
/// cache — mirrors SelfAgent's persona-cache pattern, but boot-hydrated
/// rather than lazy, since Reasoning needs the full triple list on every
/// selection prompt.
///
/// Parquet has no in-place append: each write reads the target category
/// file (if any), appends the new rows in memory, and rewrites the whole
/// file as a single row group. Fine at this scale — a persona's own
/// knowledge base, not a data lake. Uses Parquet.Net's high-level
/// ParquetSerializer (POCO row classes) rather than the raw row-group API.
/// </summary>
public sealed class ParquetArchiveStore : IArchiveStore
{
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

    private sealed class IndexRow
    {
        public string Category { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Subtopic { get; set; } = "";
    }

    private readonly string _directory;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly object _indexLock = new();
    private readonly HashSet<ArchiveTriple> _index;

    public ParquetArchiveStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _index = LoadIndexAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<ArchiveTriple> Index
    {
        get { lock (_indexLock) { return [.. _index]; } }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchiveTriple triple, int maxRows, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<ArchiveRecord> records;
        try
        {
            records = await ReadRecordsAsync(CategoryPath(triple.Category), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }

        return records
            .Where(r => string.Equals(r.Topic, triple.Topic, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Subtopic, triple.Subtopic, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Importance)
            .Take(maxRows)
            .ToList();
    }

    public async Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var group in records.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase))
            {
                var path = CategoryPath(group.Key);
                var existing = await ReadRecordsAsync(path, cancellationToken).ConfigureAwait(false);
                existing.AddRange(group);
                await WriteRecordsAsync(path, existing, cancellationToken).ConfigureAwait(false);
            }

            var newTriples = new List<ArchiveTriple>();
            lock (_indexLock)
            {
                foreach (var record in records)
                {
                    if (_index.Add(record.Triple))
                    {
                        newTriples.Add(record.Triple);
                    }
                }
            }

            if (newTriples.Count > 0)
            {
                await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string CategoryPath(string category) => Path.Combine(_directory, $"{category}.parquet");

    private string IndexPath => Path.Combine(_directory, "index.parquet");

    private async Task<HashSet<ArchiveTriple>> LoadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexPath))
        {
            return [];
        }

        var result = await ParquetSerializer.DeserializeAsync<IndexRow>(IndexPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. result.Data.Select(r => new ArchiveTriple(r.Category, r.Topic, r.Subtopic))];
    }

    private async Task WriteIndexAsync(CancellationToken cancellationToken)
    {
        List<ArchiveTriple> snapshot;
        lock (_indexLock)
        {
            snapshot = [.. _index];
        }

        var rows = snapshot.Select(t => new IndexRow { Category = t.Category, Topic = t.Topic, Subtopic = t.Subtopic });
        await ParquetSerializer.SerializeAsync(rows, IndexPath, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<ArchiveRecord>> ReadRecordsAsync(string path, CancellationToken cancellationToken)
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

    private static async Task WriteRecordsAsync(string path, List<ArchiveRecord> records, CancellationToken cancellationToken)
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
