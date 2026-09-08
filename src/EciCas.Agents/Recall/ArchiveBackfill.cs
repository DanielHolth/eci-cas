namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// Gives every archive row a vector under the embedder currently configured.
///
/// Narrowing is all-or-nothing per pair: a file is only swept by cosine when
/// every row in it carries a current vector, so one bare row costs the whole
/// pair its ranking and drops it back to chunk-and-pick. Rows written before
/// the embedder existed, written while it was down, or stamped by a model
/// that has since been swapped are all that same bare row.
///
/// So this runs at boot, next to the recency trim, rather than living in an
/// operator tool: a deployment that needs someone to remember a maintenance
/// command is a deployment where the vectors are off and nothing says so.
/// It is free when the archive is current -- one read per pair file and no
/// writes -- and it cannot stamp the wrong ModelId, because it takes the
/// embedder the host itself resolved rather than a path repeated in a script.
/// </summary>
public static class ArchiveBackfill
{
    /// <summary>
    /// Every parquet in the archive, the recency lane and each profile tier
    /// included: a lane row is read like any other and needs its vector just
    /// as much. Reports rows and files touched; both zero is the ordinary
    /// case on a warm archive.
    /// </summary>
    public static async Task<(int Rows, int Files)> RunAsync(
        string directory,
        IEmbeddingProvider embeddings,
        Action<string, int>? onFile,
        CancellationToken cancellationToken)
    {
        if (!embeddings.Available || !Directory.Exists(directory))
        {
            return (0, 0);
        }

        var modelId = embeddings.ModelId;
        var rows = 0;
        var files = 0;

        foreach (var file in Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories))
        {
            var records = await ParquetArchiveStore.ReadRecordsAsync(file, cancellationToken).ConfigureAwait(false);
            var pending = records
                .Select((r, i) => (Record: r, Index: i))
                .Where(x => !x.Record.HasVector(modelId))
                .ToList();

            if (pending.Count == 0)
            {
                continue;
            }

            var vectors = await embeddings.EmbedAsync(
                [.. pending.Select(x => x.Record.EmbeddedText)], EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);

            // Short of a vector each, the pair would be left half-stamped:
            // still all-or-nothing, still cold, and now harder to explain.
            // Skipping leaves it exactly as it was for the next boot to fix.
            if (vectors.Count != pending.Count)
            {
                continue;
            }

            var updated = records.ToList();
            for (var i = 0; i < pending.Count; i++)
            {
                var text = pending[i].Record.EmbeddedText;
                updated[pending[i].Index] = pending[i].Record with
                {
                    Embedding = vectors[i],
                    EmbeddingModelId = modelId,
                    EmbeddingHash = ArchiveEmbedding.HashOf(text),
                };
            }

            await ParquetArchiveStore.WriteRecordsAsync(file, updated, cancellationToken).ConfigureAwait(false);
            onFile?.Invoke(file, pending.Count);
            rows += pending.Count;
            files++;
        }

        return (rows, files);
    }
}
