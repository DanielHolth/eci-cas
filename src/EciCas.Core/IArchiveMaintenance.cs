namespace EciCas.Core;

/// <summary>What one maintenance pass found and did. Everything zero is the
/// ordinary case on a warm archive, and is what lets the caller stay quiet.</summary>
/// <param name="EmbeddedRows">Rows given a vector they were missing.</param>
/// <param name="RewrittenFiles">Pair files rewritten to carry those vectors.</param>
/// <param name="ModelId">The embedder that stamped them, or null when none was available.</param>
public readonly record struct ArchiveMaintenanceReport(int EmbeddedRows, int RewrittenFiles, string? ModelId);

/// <summary>
/// The archive's own upkeep — backfilling vectors, dropping what has aged
/// out of the recency lane — as a thing that can be asked for rather than a
/// pair of methods only reachable by holding the concrete store.
///
/// It exists because the repairs write the same files the registered
/// <see cref="IArchiveStore"/> is reading, so whoever rewrites them also has
/// to invalidate what was cached from the old copy. That coupling is real;
/// this is where it is allowed to live, so that no caller has to know about
/// it. Boot runs this; the roadmap's recovery agent will run the same call.
/// </summary>
public interface IArchiveMaintenance
{
    Task<ArchiveMaintenanceReport> RunAsync(CancellationToken cancellationToken);
}
