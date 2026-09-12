using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// The pass owns the coupling nobody else should have to know about: it
/// rewrites the files the store is already reading, so the invalidation
/// belongs to it and not to its caller.
/// </summary>
public class ParquetArchiveMaintenanceTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "eci-maint-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ArchiveRecord Row(string key) => new(
        Category: "user", Topic: "life", Subtopic: "home", Subject: "daniel", Key: key, Value: key + "-value",
        Timestamp: DateTimeOffset.UtcNow, Domain: ArchiveDomain.External, Importance: 0.5);

    [Fact]
    public async Task ARowWrittenBeforeTheEmbedderExistedComesBackVectored()
    {
        var directory = TempDirectory();
        var store = new ParquetArchiveStore(directory);
        await store.WriteAsync([Row("city")], CancellationToken.None);

        var report = await new ParquetArchiveMaintenance(store, directory,
            new StubEmbeddings(_ => [1f, 0f])).RunAsync(CancellationToken.None);

        Assert.True(report.EmbeddedRows > 0);
        Assert.True(report.RewrittenFiles > 0);
        Assert.Equal("stub", report.ModelId);

        // Read through the same instance that held the pre-backfill copy: if the
        // pass had left the invalidation to its caller, this would still be bare.
        var read = await store.LookupAsync(new ArchivePair("user", "life"), CancellationToken.None);
        Assert.All(read, r => Assert.NotNull(r.Embedding));

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// The offline tier has no embedder at all, and boot still runs this. It has
    /// to be a no-op that reports one rather than a failure.
    /// </summary>
    [Fact]
    public async Task WithNoEmbedderItStillRuns_AndClaimsNothing()
    {
        var directory = TempDirectory();
        var store = new ParquetArchiveStore(directory);
        await store.WriteAsync([Row("city")], CancellationToken.None);

        var report = await new ParquetArchiveMaintenance(store, directory, new StubEmbeddings())
            .RunAsync(CancellationToken.None);

        Assert.Equal(0, report.EmbeddedRows);
        Assert.Equal(0, report.RewrittenFiles);
        Assert.Null(report.ModelId);

        Directory.Delete(directory, recursive: true);
    }
}
