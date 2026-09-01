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

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, string? profileId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = _records
            .Select(r => r.Record)
            .Where(r => r.Pair == pair)
            .OrderByDescending(r => r.Importance)
            .ToList();
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
