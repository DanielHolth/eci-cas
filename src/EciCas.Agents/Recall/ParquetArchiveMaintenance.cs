namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// Upkeep for the parquet archive: backfill first, so the recency lane is
/// trimmed from vectored rows rather than bare ones, and invalidate in
/// between, because the store has been reading the very files the backfill
/// just rewrote.
///
/// Takes the concrete store rather than <see cref="IArchiveStore"/> on
/// purpose. The decorator chain above it stamps vectors on the way in and
/// owns no files; this owns the files and nothing else, which is the only
/// reason the two can safely disagree about what is cached.
/// </summary>
public sealed class ParquetArchiveMaintenance(
    ParquetArchiveStore store,
    string directory,
    IEmbeddingProvider embeddings) : IArchiveMaintenance
{
    public async Task<ArchiveMaintenanceReport> RunAsync(CancellationToken cancellationToken)
    {
        var backfilled = await ArchiveBackfill.RunAsync(directory, embeddings, onFile: null, cancellationToken);
        if (backfilled.Files > 0)
        {
            store.Invalidate();
        }

        // The lane reaches back a year and the trim is a boot-time job so no turn
        // pays for it: a write appends, and only this drops what has aged out.
        // Nothing is lost by it — the lane is a view of rows the pair files still
        // hold.
        await store.TrimRecentAsync(cancellationToken);

        return new ArchiveMaintenanceReport(backfilled.Rows, backfilled.Files,
            embeddings.Available ? embeddings.ModelId : null);
    }
}
