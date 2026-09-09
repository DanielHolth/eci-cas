using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// In-memory IArchiveStore fake for the knowledge-swarm schema — no file I/O.
/// Records the profile each write arrived under so a test can assert the
/// scoping without reaching for real Parquet; reads are unscoped, since the
/// tiering rule itself is ParquetArchiveStore's to prove.
/// </summary>
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly List<(string? ProfileId, ArchiveRecord Record)> _records = [];
    private readonly HashSet<ArchivePair> _index = [];

    public IReadOnlyList<ArchivePair> IndexFor(string? profileId) => [.. _index];

    /// <summary>Everything written, in write order — for asserting that a flush wrote nothing at all.</summary>
    public IReadOnlyList<ArchiveRecord> All => [.. _records.Select(r => r.Record)];

    /// <summary>Each write paired with the profile it was scoped to.</summary>
    public IReadOnlyList<(string? ProfileId, ArchiveRecord Record)> Scoped => _records;

    public long TurnsRecorded { get; private set; }

    /// <summary>
    /// Credits by address, the way the real store does, so a test can watch
    /// a row's rate climb without a directory.
    /// </summary>
    public Task RecordRecallAsync(IReadOnlyList<ArchiveRecord> recalled, string? profileId, CancellationToken cancellationToken)
    {
        TurnsRecorded++;
        var now = DateTimeOffset.UtcNow;
        var hit = recalled.Select(Address).ToHashSet();
        for (var i = 0; i < _records.Count; i++)
        {
            if (hit.Contains(Address(_records[i].Record)))
            {
                var record = _records[i].Record;
                _records[i] = (_records[i].ProfileId, record with { Hits = record.Hits + 1, LastHitAt = now });
            }
        }

        return Task.CompletedTask;
    }

    private static (string, string, string) Address(ArchiveRecord record) =>
        (record.Subtopic.ToLowerInvariant(), record.Subject.ToLowerInvariant(), record.Key.ToLowerInvariant());

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, string? profileId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = _records
            .Select(r => r.Record)
            .Where(r => r.Pair == pair)
            .OrderByDescending(r => r.Importance)
            .ToList();
        return Task.FromResult(results);
    }

    /// <summary>Write order is the fake's clock: newest last in, newest first out.</summary>
    public Task<IReadOnlyList<ArchiveRecord>> RecentAsync(string? profileId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = [.. _records.Select(r => r.Record).Reverse().Take(Math.Max(0, limit))];
        return Task.FromResult(results);
    }

    public Task WriteAsync(IReadOnlyList<ArchiveRecord> records, string? profileId, CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            _records.Add((profileId, record));
            _index.Add(record.Pair);
        }

        return Task.CompletedTask;
    }
}
