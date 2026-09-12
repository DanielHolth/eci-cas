using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// In-memory IArchiveStore fake for the knowledge-swarm schema — no file I/O.
/// </summary>
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly List<ArchiveRecord> _records = [];
    private readonly HashSet<ArchivePair> _index = [];

    public IReadOnlyList<ArchivePair> IndexFor() => [.. _index];

    /// <summary>Everything written, in write order — for asserting that a flush wrote nothing at all.</summary>
    public IReadOnlyList<ArchiveRecord> All => [.. _records];

    public long TurnsRecorded { get; private set; }

    /// <summary>
    /// Credits by address, the way the real store does, so a test can watch
    /// a row's rate climb without a directory.
    /// </summary>
    public Task RecordRecallAsync(IReadOnlyList<ArchiveRecord> recalled, CancellationToken cancellationToken)
    {
        TurnsRecorded++;
        var now = DateTimeOffset.UtcNow;
        var hit = recalled.Select(Address).ToHashSet();
        for (var i = 0; i < _records.Count; i++)
        {
            if (hit.Contains(Address(_records[i])))
            {
                var record = _records[i];
                _records[i] = record with { Hits = record.Hits + 1, LastHitAt = now };
            }
        }

        return Task.CompletedTask;
    }

    private static (string, string, string) Address(ArchiveRecord record) =>
        (record.Subtopic.ToLowerInvariant(), record.Subject.ToLowerInvariant(), record.Key.ToLowerInvariant());

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = _records
            .Where(r => r.Pair == pair)
            .OrderByDescending(r => r.Importance)
            .ToList();
        return Task.FromResult(results);
    }

    /// <summary>Write order is the fake's clock: newest last in, newest first out.</summary>
    public Task<IReadOnlyList<ArchiveRecord>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = [.. _records.AsEnumerable().Reverse().Take(Math.Max(0, limit))];
        return Task.FromResult(results);
    }

    public Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            _records.Add(record);
            _index.Add(record.Pair);
        }

        return Task.CompletedTask;
    }
}
