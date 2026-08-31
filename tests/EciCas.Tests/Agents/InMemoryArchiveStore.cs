using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>In-memory IArchiveStore fake for the knowledge-swarm schema — no file I/O.</summary>
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly List<ArchiveRecord> _records = [];
    private readonly HashSet<ArchiveTriple> _index = [];

    public IReadOnlyList<ArchiveTriple> Index => [.. _index];

    /// <summary>Everything written, in write order — for asserting that a flush wrote nothing at all.</summary>
    public IReadOnlyList<ArchiveRecord> All => _records;

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchiveTriple triple, int maxRows, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> results = _records
            .Where(r => r.Triple == triple)
            .OrderByDescending(r => r.Importance)
            .Take(maxRows)
            .ToList();
        return Task.FromResult(results);
    }

    public Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken)
    {
        _records.AddRange(records);
        foreach (var record in records)
        {
            _index.Add(record.Triple);
        }

        return Task.CompletedTask;
    }
}
