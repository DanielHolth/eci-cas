using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>In-memory IArchiveStore fake for the knowledge-swarm schema — no file I/O.</summary>
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly List<ArchiveRecord> _records = [];
    private readonly HashSet<ArchivePair> _index = [];

    public IReadOnlyList<ArchivePair> Index => [.. _index];

    /// <summary>Everything written, in write order — for asserting that a flush wrote nothing at all.</summary>
    public IReadOnlyList<ArchiveRecord> All => _records;

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = _records
            .Where(r => r.Pair == pair)
            .OrderByDescending(r => r.Importance)
            .ToList();
        return Task.FromResult(results);
    }

    public Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        _records.AddRange(records);
        foreach (var record in records)
        {
            _index.Add(record.Pair);
        }

        return Task.CompletedTask;
    }
}
