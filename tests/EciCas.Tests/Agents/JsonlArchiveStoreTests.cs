using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

public class JsonlArchiveStoreTests
{
    [Fact]
    public async Task LookupAsync_WithNoFile_ReturnsEmpty()
    {
        var store = new JsonlArchiveStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl"));
        var results = await store.LookupAsync(["self/identity"], maxPerPath: 3, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task WriteThenLookup_ReturnsNewestFirst_CappedPerPath()
    {
        var store = new JsonlArchiveStore(Path.GetTempFileName());

        await store.WriteAsync(
            [
                new ArchiveRecord("turn", "first", DateTimeOffset.UtcNow),
                new ArchiveRecord("turn", "second", DateTimeOffset.UtcNow),
                new ArchiveRecord("other", "unrelated", DateTimeOffset.UtcNow),
            ],
            CancellationToken.None);

        var results = await store.LookupAsync(["turn"], maxPerPath: 1, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("second", results[0].Content);
    }
}
