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

    [Fact]
    public async Task WriteAsync_KeepsAWindowOfHistoryPerPath_NotJustTheNewest()
    {
        var file = Path.GetTempFileName();
        var store = new JsonlAgentStateStore(file, historyPerPath: 3);

        for (var i = 0; i < 10; i++)
        {
            await store.WriteAsync(
                [
                    new AgentStateRecord("impulse/drive", $"state{i}", DateTimeOffset.UtcNow),
                    new AgentStateRecord("assistant/persona", $"persona{i}", DateTimeOffset.UtcNow),
                ],
                CancellationToken.None);
        }

        var drive = await store.LookupAsync(["impulse/drive"], maxPerPath: 100, CancellationToken.None);
        Assert.Equal(["state9", "state8", "state7"], drive.Select(r => r.Content));

        // The window is per path, so a busy path cannot evict a quiet one.
        var persona = await store.LookupAsync(["assistant/persona"], maxPerPath: 100, CancellationToken.None);
        Assert.Equal("persona9", persona[0].Content);

        // Bounded, and bounded by paths times window rather than by writes.
        Assert.Equal(6, File.ReadAllLines(file).Length);
    }

    [Fact]
    public async Task WriteAsync_LeavesLinesItCannotParseAlone()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "not json at all" + Environment.NewLine);
        var store = new JsonlAgentStateStore(file, historyPerPath: 1);

        await store.WriteAsync([new AgentStateRecord("turn", "kept", DateTimeOffset.UtcNow)], CancellationToken.None);

        // Trimming decides what to drop by path; a line with no path it can
        // read is not a candidate, so it survives rather than being guessed at.
        Assert.Contains("not json at all", File.ReadAllLines(file));
    }
}
