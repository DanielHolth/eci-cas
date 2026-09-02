using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The vector half of memory: Reflection writes passages, Librarian matches
/// them and turns them into leads. Asserts outcome — what got written, what
/// got selected — never the order agents ran in.
/// </summary>
public class PassageMemoryTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest(string agent, string substrateClass) =>
        Options.Create(new AgentSubstrateManifest { Agents = { [agent] = new AgentSubstrateEntry { Class = substrateClass } } });

    /// <summary>Orthogonal unit vectors, so a test can make two texts match exactly or not at all.</summary>
    private static float[] Unit(int i)
    {
        var v = new float[4];
        v[i] = 1f;
        return v;
    }

    [Fact]
    public async Task PassageStore_RoundTripsPairsAndVectors_AndRevisitReplacesTheOldRow()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ParquetPassageStore(directory);
            var first = new Passage("a", "missed the family record", [new ArchivePair("person", "family")],
                DateTimeOffset.UtcNow.AddMinutes(-1), Unit(0));
            await store.WriteAsync([first], null, CancellationToken.None);

            // A second store over the same directory: proves the vector and
            // the pairs survived the file, not just the in-memory cache.
            var reopened = new ParquetPassageStore(directory);
            var hit = Assert.Single(await reopened.SearchAsync(Unit(0), 5, 0.5, CancellationToken.None));
            Assert.Equal("missed the family record", hit.Passage.Text);
            Assert.Equal(new ArchivePair("person", "family"), Assert.Single(hit.Passage.Pairs));
            Assert.Equal(1.0, hit.Score, 5);

            // The revisit keeps the id and the timestamp, so it replaces the
            // note rather than accumulating beside it — and "latest" still
            // means the newest event-series, not the newest edit.
            var revised = first with { Text = "should have read person/family first" };
            var current = new Passage("b", "missed the deadline context", [], DateTimeOffset.UtcNow, Unit(1));
            await reopened.WriteAsync([revised, current], "a", CancellationToken.None);

            var all = await new ParquetPassageStore(directory).SearchAsync(Unit(0), 5, -1.0, CancellationToken.None);
            Assert.Equal(2, all.Count);
            Assert.Equal("should have read person/family first", all.Single(h => h.Passage.Id == "a").Passage.Text);
            Assert.Equal("b", (await new ParquetPassageStore(directory).LatestAsync(CancellationToken.None))!.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchBelowMinScore_ReturnsNothing()
    {
        var store = new InMemoryPassageStore();
        await store.WriteAsync([new Passage("a", "note", [], DateTimeOffset.UtcNow, Unit(0))], null, CancellationToken.None);

        Assert.Empty(await store.SearchAsync(Unit(1), 5, 0.45, CancellationToken.None));
    }

    /// <summary>
    /// The whole point of the corpus: a matched passage adds the pair it named
    /// to the selection, on top of whatever the selection call picked, and its
    /// text rides along to reach Intent through Recall.
    /// </summary>
    [Fact]
    public async Task AMatchedPassage_AddsItsPairsToTheSelection_AndCarriesItsText()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);

        var store = new InMemoryArchiveStore();
        await store.WriteAsync([
            new ArchiveRecord("person", "family", "son", "marcus", "birthdate", "2020-08-28", DateTimeOffset.UtcNow),
            .. Enumerable.Range(0, 3).Select(i =>
                new ArchiveRecord("world", $"topic{i}", "misc", "misc", "key", "value", DateTimeOffset.UtcNow))],
            null, CancellationToken.None);

        var passages = new InMemoryPassageStore();
        await passages.WriteAsync(
            [new Passage("a", "should have read the family record", [new ArchivePair("person", "family")], DateTimeOffset.UtcNow, Unit(0))],
            null, CancellationToken.None);

        // The selection call picks index 1 (world/topic0); the passage
        // contributes person/family, which it did not pick.
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("1", TimeSpan.Zero, 5, 0m)));
        var agent = new LibrarianAgent(bus, activity, NullLogger<LibrarianAgent>.Instance, store, substrate,
            Manifest("Librarian", "fast-medium"), Options.Create(new LibrarianOptions { MaxSelectedPairs = 1 }),
            new StubEmbeddings(_ => Unit(0)), passages, Options.Create(new PassageOptions()));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?")), CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        var pairs = selection!.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey)!;
        Assert.Contains(new ArchivePair("person", "family"), pairs);
        Assert.Equal("should have read the family record",
            Assert.Single(selection.Meta.Get<IReadOnlyList<string>>(LibrarianAgent.PassagesKey)!));
    }

    /// <summary>
    /// A pair whose last row was deleted took its file, and therefore its
    /// index entry, with it. Trusting a stale pointer would send Recall to
    /// read nothing; resolving against the live index drops it silently.
    /// </summary>
    [Fact]
    public async Task APassagePointingAtAPairThatNoLongerExists_ContributesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var selections = bus.Subscribe(Topics.SelectedPairs);

        var store = new InMemoryArchiveStore();
        await store.WriteAsync([new ArchiveRecord("world", "weather", "misc", "misc", "key", "value", DateTimeOffset.UtcNow)],
            null, CancellationToken.None);

        var passages = new InMemoryPassageStore();
        await passages.WriteAsync(
            [new Passage("a", "stale lead", [new ArchivePair("person", "deleted")], DateTimeOffset.UtcNow, Unit(0))],
            null, CancellationToken.None);

        var agent = new LibrarianAgent(bus, activity, NullLogger<LibrarianAgent>.Instance, store,
            new StubSubstrate(_ => throw new InvalidOperationException("index fits under the cap, so this is never called")),
            Manifest("Librarian", "fast-medium"), Options.Create(new LibrarianOptions()),
            new StubEmbeddings(_ => Unit(0)), passages, Options.Create(new PassageOptions()));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "anything on file?")), CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.DoesNotContain(new ArchivePair("person", "deleted"),
            selection!.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey)!);
        Assert.Equal("stale lead", Assert.Single(selection.Meta.Get<IReadOnlyList<string>>(LibrarianAgent.PassagesKey)!));
    }

    /// <summary>Recall is the roster slot between the match and Intent, so the text has to survive the hop.</summary>
    [Fact]
    public async Task Recall_ForwardsMatchedPassagesOntoItsAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, new InMemoryArchiveStore(),
            new StubSubstrate(_ => throw new InvalidOperationException("no pairs selected, so this is never called")),
            Manifest("Recall", "fast-low"), Options.Create(new RecallOptions()));

        var selection = Envelope.Create(Topics.SelectedPairs, "Librarian", Severity.Neutral,
            MetaBag.Empty
                .With(LibrarianAgent.SelectedPairsKey, (IReadOnlyList<ArchivePair>)[])
                .With(LibrarianAgent.PassagesKey, (IReadOnlyList<string>)["should have read the family record"]));
        await agent.HandleAsync(selection, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("should have read the family record",
            Assert.Single(advisory!.Meta.Get<IReadOnlyList<string>>(RecallAgent.RecalledPassagesKey)!));
    }

    [Fact]
    public async Task Reflection_WritesTheBatchNote_AndTheRevisitReplacesThePreviousOne()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);

        var passages = new InMemoryPassageStore();
        await passages.WriteAsync(
            [new Passage("old", "first thought", [], DateTimeOffset.UtcNow.AddHours(-1), Unit(0))],
            null, CancellationToken.None);

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "mood|curious\nmissed|person/family|needed the birthdate to answer\nrevisit|person/family|first thought, sharpened",
            TimeSpan.Zero, 5, 0m)));

        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new InMemoryArchiveStore(),
            new JsonlAgentStateStore(Path.GetTempFileName()), substrate,
            Manifest("Reflection", "slow-medium"), Options.Create(new ReflectionOptions { BatchSize = 1 }),
            passages, new StubEmbeddings(_ => Unit(1)));

        await agent.HandleAsync(Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral, MetaBag.Empty), CancellationToken.None);

        Assert.Equal(2, passages.Passages.Count);
        Assert.Equal("first thought, sharpened", passages.Passages.Single(p => p.Id == "old").Text);

        var current = passages.Passages.Single(p => p.Id != "old");
        Assert.Equal("needed the birthdate to answer", current.Text);
        Assert.Equal(new ArchivePair("person", "family"), Assert.Single(current.Pairs));
    }

    /// <summary>
    /// No embedder means no vector to store a passage under, so the corpus is
    /// left alone rather than filled with rows nothing can ever match.
    /// </summary>
    [Fact]
    public async Task WithNoEmbedder_ReflectionWritesNoPassages()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var passages = new InMemoryPassageStore();

        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new InMemoryArchiveStore(),
            new JsonlAgentStateStore(Path.GetTempFileName()),
            new StubSubstrate(_ => Task.FromResult(new SubstrateResult("mood|dull\nmissed|person/family|note", TimeSpan.Zero, 5, 0m))),
            Manifest("Reflection", "slow-medium"), Options.Create(new ReflectionOptions { BatchSize = 1 }),
            passages, new StubEmbeddings());

        await agent.HandleAsync(Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral, MetaBag.Empty), CancellationToken.None);

        Assert.Empty(passages.Passages);
    }
}
