using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

public class JsonlAgentStateStoreTests
{
    [Fact]
    public async Task LookupAsync_WithNoFile_ReturnsEmpty()
    {
        var store = new JsonlAgentStateStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl"));
        var results = await store.LookupAsync(["assistant/persona"], maxPerPath: 3, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task WriteThenLookup_ReturnsNewestFirst_CappedPerPath()
    {
        var store = new JsonlAgentStateStore(Path.GetTempFileName());

        await store.WriteAsync(
            [
                new AgentStateRecord("turn", "first", DateTimeOffset.UtcNow),
                new AgentStateRecord("turn", "second", DateTimeOffset.UtcNow),
                new AgentStateRecord("other", "unrelated", DateTimeOffset.UtcNow),
            ],
            CancellationToken.None);

        var results = await store.LookupAsync(["turn"], maxPerPath: 1, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("second", results[0].Content);
    }
}
