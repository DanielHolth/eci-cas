namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// Stamps a vector onto every row on its way to disk, and is otherwise the
/// store it wraps.
///
/// A decorator rather than a call inside Cataloger because writing is not
/// Cataloger's alone: Reflection writes, PersonaName writes, a tool import
/// writes, and every one of them would otherwise have to acquire an embedder
/// and remember to use it. A row that reaches disk without a vector is not
/// broken but it is expensive - it makes its whole pair fall back to the
/// whole-file read path, since a pair is only swept by cosine when every row
/// in it carries a current vector.
///
/// Embedding is not free and it is not always possible. When the provider is
/// unavailable - the offline tier, no weights on disk, a dead endpoint - this
/// writes exactly what it was given and says nothing: unembedded rows are the
/// pre-vector behaviour, which still works.
/// </summary>
public sealed class EmbeddingArchiveStore(IArchiveStore inner, IEmbeddingProvider embeddings) : IArchiveStore
{
    public IReadOnlyList<ArchivePair> IndexFor(string? profileId) => inner.IndexFor(profileId);

    public Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, string? profileId, CancellationToken cancellationToken) =>
        inner.LookupAsync(pair, profileId, cancellationToken);

    public long TurnsRecorded => inner.TurnsRecorded;

    public Task RecordRecallAsync(IReadOnlyList<ArchiveRecord> recalled, string? profileId, CancellationToken cancellationToken) =>
        inner.RecordRecallAsync(recalled, profileId, cancellationToken);

    public Task<IReadOnlyList<ArchiveRecord>> RecentAsync(string? profileId, int limit, CancellationToken cancellationToken) =>
        inner.RecentAsync(profileId, limit, cancellationToken);

    public async Task WriteAsync(IReadOnlyList<ArchiveRecord> records, string? profileId, CancellationToken cancellationToken) =>
        await inner.WriteAsync(
            await EmbeddedAsync(records, cancellationToken).ConfigureAwait(false),
            profileId,
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ArchiveRecord>> EmbeddedAsync(
        IReadOnlyList<ArchiveRecord> records,
        CancellationToken cancellationToken)
    {
        var modelId = embeddings.ModelId;
        if (!embeddings.Available || modelId.Length == 0 || records.Count == 0)
        {
            return records;
        }

        // Rows that already carry a current vector are left alone. This is
        // the ordinary case for a re-write of an untouched pair, and skipping
        // them keeps a restatement of one fact from re-embedding its
        // neighbours.
        var pending = records
            .Select((r, i) => (Record: r, Index: i))
            .Where(x => !x.Record.HasVector(modelId))
            .ToList();

        if (pending.Count == 0)
        {
            return records;
        }

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await embeddings
                .EmbedAsync([.. pending.Select(x => x.Record.EmbeddedText)], EmbeddingKind.Passage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A fact reaching the archive matters more than a fact reaching
            // it searchable: an embedder that fell over must not take the
            // write with it. The row lands unembedded and the backfill or the
            // next restatement picks it up.
            return records;
        }

        if (vectors.Count != pending.Count)
        {
            return records;
        }

        var stamped = records.ToArray();
        for (var i = 0; i < pending.Count; i++)
        {
            var (record, at) = pending[i];
            var text = record.EmbeddedText;
            stamped[at] = record with
            {
                Embedding = vectors[i],
                EmbeddingModelId = modelId,

                // Hashed from the same string that was just embedded, not
                // from the record as it will be read back: those are the same
                // today and the check is worthless if they ever drift.
                EmbeddingHash = ArchiveEmbedding.HashOf(text),
            };
        }

        return stamped;
    }
}
