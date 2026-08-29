namespace EciCas.Core;

/// <summary>
/// Owns files, schema, epochs, concurrency, and every method over the data —
/// including N-way parallel lookup. A library, not a bus citizen; Recall is
/// the thin bus adapter that applies tier policy on top of this. See plan
/// §3.3. Implemented in M4.
/// </summary>
public interface IArchiveStore
{
    Task<IReadOnlyList<ArchiveRecord>> LookupAsync(IReadOnlyList<string> paths, int maxPerPath, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken);
}

public sealed record ArchiveRecord(string Path, string Content, DateTimeOffset Timestamp);
