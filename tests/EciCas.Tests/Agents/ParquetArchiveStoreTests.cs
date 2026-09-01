using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// The file name carries the pair, so name encoding is not cosmetic — it is
/// how the index survives a restart. These cover the round trip, the
/// characters a filesystem would otherwise reject, and the guarantee that a
/// deep pair is never silently trimmed on read.
/// </summary>
public class ParquetArchiveStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"eci-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ArchiveRecord Record(string category, string topic, string subtopic, string key, string value, double importance = 0.5) =>
        new(category, topic, subtopic, "subject", key, value, DateTimeOffset.UtcNow, ArchiveDomain.External, importance);

    [Theory]
    [InlineData("person")]
    [InlineData("trip plans")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
    [InlineData("with~tilde")]
    [InlineData("trailing.")]
    [InlineData("percent%20already")]
    [InlineData("blåbær 日本語")]
    public void EscapeRoundTrips(string value) =>
        Assert.Equal(value, ParquetArchiveStore.Unescape(ParquetArchiveStore.Escape(value)));

    [Fact]
    public void EscapedNameContainsNoCharacterAFilesystemWouldReject()
    {
        var escaped = ParquetArchiveStore.Escape("a/b\\c:d*e?f\"g<h>i|j and a space");
        Assert.DoesNotContain(escaped, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.DoesNotContain('~', escaped);
    }

    /// <summary>A tilde inside either half must not read as the separator.</summary>
    [Fact]
    public void PairWithTildeInItsFieldsStillDecodesToTheSamePair()
    {
        var pair = new ArchivePair("cat~egory", "top~ic");
        var name = Path.GetFileNameWithoutExtension(ParquetArchiveStore.PairPathFor(_directory, pair));

        Assert.True(ParquetArchiveStore.TryDecodeName(name, out var decoded));
        Assert.Equal(pair, decoded);
    }

    [Fact]
    public async Task IndexIsRecoveredFromFileNames_WithNoIndexFile()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([
            Record("person", "family", "son", "birthdate", "2020-08-28"),
            Record("event", "wedding: oslo", "venue", "location", "drammen kirke"),
        ], null, CancellationToken.None);

        Assert.DoesNotContain(Directory.EnumerateFiles(_directory), f => Path.GetFileName(f).StartsWith("index", StringComparison.OrdinalIgnoreCase));

        var reopened = new ParquetArchiveStore(_directory);
        Assert.Contains(new ArchivePair("person", "family"), reopened.IndexFor(null));
        Assert.Contains(new ArchivePair("event", "wedding: oslo"), reopened.IndexFor(null));
        Assert.Equal(2, reopened.IndexFor(null).Count);
    }

    [Fact]
    public async Task EachPairGetsItsOwnFile()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([
            Record("person", "family", "son", "birthdate", "2020-08-28"),
            Record("person", "work", "employer", "role", "engineer"),
        ], null, CancellationToken.None);

        Assert.Equal(2, Directory.EnumerateFiles(_directory, "*.parquet").Count());
    }

    /// <summary>
    /// The point of dropping the per-topic cap: one subtopic discussed at
    /// length keeps every row, and Recall chunks it rather than the store
    /// truncating it.
    /// </summary>
    [Fact]
    public async Task LookupReturnsEveryRowUnderAPair_Uncapped()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync(
            [.. Enumerable.Range(0, 250).Select(i => Record("science", "thermodynamics", "entropy", $"note{i}", $"value {i}"))],
            null, CancellationToken.None);

        var rows = await store.LookupAsync(new ArchivePair("science", "thermodynamics"), null, CancellationToken.None);
        Assert.Equal(250, rows.Count);
    }

    [Fact]
    public async Task LookupOrdersByImportanceDescending()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([
            Record("person", "family", "son", "low", "l", importance: 0.1),
            Record("person", "family", "son", "high", "h", importance: 0.9),
            Record("person", "family", "daughter", "mid", "m", importance: 0.5),
        ], null, CancellationToken.None);

        var rows = await store.LookupAsync(new ArchivePair("person", "family"), null, CancellationToken.None);
        Assert.Equal(["high", "mid", "low"], rows.Select(r => r.Key));
    }

    [Fact]
    public async Task LookupOfAnUnknownPairIsEmpty_NotAnError()
    {
        var store = new ParquetArchiveStore(_directory);
        Assert.Empty(await store.LookupAsync(new ArchivePair("nothing", "here"), null, CancellationToken.None));
    }

    /// <summary>
    /// Writes to different pairs run in parallel against different files, so
    /// this is the guard that parallelism didn't cost anyone their rows.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesAcrossPairsAllLand()
    {
        var store = new ParquetArchiveStore(_directory);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.WriteAsync([Record("person", $"topic{i % 4}", "sub", $"key{i}", $"value {i}")], null, CancellationToken.None)));

        var total = 0;
        foreach (var pair in store.IndexFor(null))
        {
            total += (await store.LookupAsync(pair, null, CancellationToken.None)).Count;
        }

        Assert.Equal(20, total);
        Assert.Equal(4, store.IndexFor(null).Count);
    }

    // --- profile tiering -------------------------------------------------

    [Fact]
    public async Task PersonalFactsLandInTheProfileDirectory_SharedCategoriesStayShared()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([
            Record("person", "family", "son", "birthdate", "2020-08-28"),
            Record("system", "identity", "persona", "name", "morrow"),
        ], "daniel", CancellationToken.None);

        var profileDirectory = ParquetArchiveStore.ProfileDirectoryFor(_directory, "daniel");
        Assert.Single(Directory.EnumerateFiles(profileDirectory, "*.parquet"));
        Assert.Single(Directory.EnumerateFiles(_directory, "*.parquet"));
        Assert.Contains(new ArchivePair("person", "family"), ParquetArchiveStore.PairsIn(profileDirectory));
        Assert.Contains(new ArchivePair("system", "identity"), ParquetArchiveStore.PairsIn(_directory));
    }

    [Fact]
    public async Task OneProfileNeverSeesAnother()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([Record("person", "family", "son", "name", "aksel")], "daniel", CancellationToken.None);
        await store.WriteAsync([Record("person", "pets", "dog", "name", "rex")], "ada", CancellationToken.None);

        Assert.Contains(new ArchivePair("person", "family"), store.IndexFor("daniel"));
        Assert.DoesNotContain(new ArchivePair("person", "pets"), store.IndexFor("daniel"));
        Assert.Empty(await store.LookupAsync(new ArchivePair("person", "pets"), "daniel", CancellationToken.None));
    }

    [Fact]
    public async Task ReadsUnionSharedAndProfile_WithTheProfileWinningOnCollision()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([
            Record("person", "family", "son", "name", "shared-answer"),
            Record("person", "family", "son", "city", "oslo"),
        ], null, CancellationToken.None);
        await store.WriteAsync([Record("person", "family", "son", "name", "profile-answer")], "daniel", CancellationToken.None);

        var rows = await store.LookupAsync(new ArchivePair("person", "family"), "daniel", CancellationToken.None);
        Assert.Equal("profile-answer", Assert.Single(rows, r => r.Key == "name").Value);
        Assert.Equal(2, rows.Count);

        // The shared tier alone is what a profile-less turn still sees.
        var unscoped = await store.LookupAsync(new ArchivePair("person", "family"), null, CancellationToken.None);
        Assert.Equal("shared-answer", Assert.Single(unscoped, r => r.Key == "name").Value);
    }

    [Fact]
    public async Task SharedCategoriesAreConfigurable()
    {
        var store = new ParquetArchiveStore(_directory, ["world"]);
        await store.WriteAsync([
            Record("world", "geography", "norway", "capital", "oslo"),
            Record("system", "identity", "persona", "name", "morrow"),
        ], "daniel", CancellationToken.None);

        Assert.Contains(new ArchivePair("world", "geography"), ParquetArchiveStore.PairsIn(_directory));
        Assert.Contains(new ArchivePair("system", "identity"),
            ParquetArchiveStore.PairsIn(ParquetArchiveStore.ProfileDirectoryFor(_directory, "daniel")));
    }

    [Fact]
    public async Task RestatingAFactReplacesItRatherThanStackingASecondRow()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new ParquetArchiveStore(directory);
        var pair = new ArchivePair("person", "daniel");

        await store.WriteAsync([new ArchiveRecord("person", "daniel", "home", "daniel", "city", "oslo", DateTimeOffset.UtcNow)], null, CancellationToken.None);
        await store.WriteAsync([new ArchiveRecord("person", "daniel", "home", "Daniel", "City", "bergen", DateTimeOffset.UtcNow)], null, CancellationToken.None);

        var rows = await store.LookupAsync(pair, null, CancellationToken.None);
        Assert.Equal("bergen", Assert.Single(rows).Value);
    }

    [Fact]
    public async Task ADuplicateInsideOneBatchLandsOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new ParquetArchiveStore(directory);
        var now = DateTimeOffset.UtcNow;

        await store.WriteAsync(
        [
            new ArchiveRecord("person", "daniel", "home", "daniel", "city", "oslo", now),
            new ArchiveRecord("person", "daniel", "home", "daniel", "city", "bergen", now),
        ], null, CancellationToken.None);

        var rows = await store.LookupAsync(new ArchivePair("person", "daniel"), null, CancellationToken.None);
        Assert.Equal("bergen", Assert.Single(rows).Value);
    }
}
