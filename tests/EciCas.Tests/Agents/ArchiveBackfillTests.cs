using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

public class ArchiveBackfillTests
{
    private static float[] Fixed(string _) => [1f, 0f, 0f, 0f];

    /// <summary>
    /// The passage corpus lives beside the pair files, in a schema with no
    /// Category column. Backfill globbed the extension and read it as a
    /// shelf row, which threw at boot — before the host could start, and
    /// only once a passage existed, so it survived every archive that had
    /// never reflected.
    /// </summary>
    [Fact]
    public async Task PassagesAtTheArchiveRoot_AreNotReadAsShelfRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eci-backfill-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var shelf = new ParquetArchiveStore(directory);
            await shelf.WriteAsync(
                [new ArchiveRecord("user", "profile", "name", "this", "value", "Daniel",
                    DateTimeOffset.UtcNow, ArchiveDomain.External, 0.5)],
                CancellationToken.None);

            await new ParquetPassageStore(directory).WriteAsync(
                [new Passage(Guid.NewGuid().ToString("n"), "a thought about the turn", [],
                    DateTimeOffset.UtcNow, Fixed(""), ModelId: "stub")],
                replacedId: null,
                CancellationToken.None);

            var (rows, files) = await ArchiveBackfill.RunAsync(
                directory, new StubEmbeddings(Fixed), onFile: null, CancellationToken.None);

            // Two files, because the pair row is mirrored into the recency
            // lane and a lane row needs its vector like any other. The third
            // file at this root — the passage — is left to the store that
            // owns it, and reads back exactly as it was written.
            Assert.Equal(2, rows);
            Assert.Equal(2, files);

            var passages = await new ParquetPassageStore(directory).AllAsync(CancellationToken.None);
            Assert.Equal("a thought about the turn", Assert.Single(passages).Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
