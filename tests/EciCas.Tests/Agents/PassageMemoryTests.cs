using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Agents.Hindsight;
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
            var first = new Passage("a", "it answers about people more warmly than about dates", [new ArchivePair("person", "family")],
                DateTimeOffset.UtcNow.AddMinutes(-1), Unit(0));
            await store.WriteAsync([first], null, CancellationToken.None);

            // A second store over the same directory: proves the vector and
            // the pairs survived the file, not just the in-memory cache.
            var reopened = new ParquetPassageStore(directory);
            var hit = Assert.Single(await reopened.SearchAsync(Unit(0), 5, 0.5, CancellationToken.None));
            Assert.Equal("it answers about people more warmly than about dates", hit.Passage.Text);
            Assert.Equal(new ArchivePair("person", "family"), Assert.Single(hit.Passage.Pairs));
            Assert.Equal(1.0, hit.Score, 5);

            // The revisit keeps the id and the timestamp, so it replaces the
            // note rather than accumulating beside it — and "latest" still
            // means the newest event-series, not the newest edit.
            var revised = first with { Text = "the warmth is for anything with a name, not only people" };
            var current = new Passage("b", "it hedges hardest when a question carries a deadline", [], DateTimeOffset.UtcNow, Unit(1));
            await reopened.WriteAsync([revised, current], "a", CancellationToken.None);

            var all = await new ParquetPassageStore(directory).SearchAsync(Unit(0), 5, -1.0, CancellationToken.None);
            Assert.Equal(2, all.Count);
            Assert.Equal("the warmth is for anything with a name, not only people", all.Single(h => h.Passage.Id == "a").Passage.Text);
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
            new StubEmbeddings(_ => Unit(0)), passages, Options.Create(new PassageOptions()), ShippedInstructions.Store);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?")), CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        var pairs = selection!.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey)!;
        Assert.Contains(new ArchivePair("person", "family"), pairs);
        Assert.Null(selection.Meta.Get<IReadOnlyList<string>>("librarian.passages"));
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
            new StubEmbeddings(_ => Unit(0)), passages, Options.Create(new PassageOptions()), ShippedInstructions.Store);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "anything on file?")), CancellationToken.None);

        Assert.True(selections.TryRead(out var selection));
        Assert.DoesNotContain(new ArchivePair("person", "deleted"),
            selection!.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey)!);

    }

    /// <summary>
    /// Hindsight is its own roster slot now: it wakes a note on the incoming
    /// text and publishes the prose itself, rather than handing it to
    /// Librarian to carry into Recall's slot. Prose and facts reach Intent by
    /// separate paths, so it can disagree with either one.
    /// </summary>
    [Fact]
    public async Task Hindsight_WakesAMatchingNote_OntoItsOwnAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        var passages = new InMemoryPassageStore();
        await passages.WriteAsync(
            [new Passage("a", "it answers about people more warmly than about dates",
                [new ArchivePair("person", "family")], DateTimeOffset.UtcNow.AddDays(-90), Unit(0))],
            null, CancellationToken.None);

        var agent = new HindsightAgent(bus, activity, NullLogger<HindsightAgent>.Instance,
            new StubEmbeddings(_ => Unit(0)), passages, Options.Create(new PassageOptions()));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "how old is marcus?")), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("Hindsight", advisory!.PublishedBy);

        // The age rides on the front: a thought from three months ago and one
        // from this morning must not read the same to Intent.
        var note = Assert.Single(advisory.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NotesKey)!);
        Assert.Equal("3 months ago: it answers about people more warmly than about dates", note);
    }

    /// <summary>
    /// A roster slot that stays silent holds the bundle open until Governance
    /// times it out, so having thought nothing is published as an answer.
    /// </summary>
    [Fact]
    public async Task Hindsight_PublishesAnEmptyAdvisory_WhenNothingWakes()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        var agent = new HindsightAgent(bus, activity, NullLogger<HindsightAgent>.Instance,
            new StubEmbeddings(_ => Unit(0)), new InMemoryPassageStore(), Options.Create(new PassageOptions()));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "anything on file?")), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Null(advisory!.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NotesKey));
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
            "mood|curious\nthought|person/family|it answers about people more warmly than about dates\nrevisit|person/family|first thought, sharpened",
            TimeSpan.Zero, 5, 0m)));

        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new InMemoryArchiveStore(),
            new JsonlAgentStateStore(Path.GetTempFileName()), substrate,
            Manifest("Reflection", "slow-medium"), Options.Create(new ReflectionOptions { BatchSize = 1 }),
            passages, new StubEmbeddings(_ => Unit(1)), ShippedInstructions.Store);

        await agent.HandleAsync(Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral, MetaBag.Empty), CancellationToken.None);

        Assert.Equal(2, passages.Passages.Count);
        Assert.Equal("first thought, sharpened", passages.Passages.Single(p => p.Id == "old").Text);

        var current = passages.Passages.Single(p => p.Id != "old");
        Assert.Equal("it answers about people more warmly than about dates", current.Text);
        Assert.Equal(new ArchivePair("person", "family"), Assert.Single(current.Pairs));
    }

    /// <summary>
    /// The lineage hop, end to end: a note Hindsight woke is carried through
    /// Intent and Governance on the meta bag, and comes out as the parent of
    /// whatever thought that turn provoked. Depth climbs so a note built on
    /// notes is distinguishable from one built on turns the persona had not
    /// already coloured.
    /// </summary>
    [Fact]
    public async Task Reflection_RecordsTheWokenNotesAsParents_AndClimbsEchoDepth()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var passages = new InMemoryPassageStore();

        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "mood|curious\nthought|person/family|a thought provoked by an older one", TimeSpan.Zero, 5, 0m)));

        var agent = new ReflectionAgent(bus, activity, NullLogger<ReflectionAgent>.Instance, new InMemoryArchiveStore(),
            new JsonlAgentStateStore(Path.GetTempFileName()), substrate,
            Manifest("Reflection", "slow-medium"), Options.Create(new ReflectionOptions { BatchSize = 1 }),
            passages, new StubEmbeddings(_ => Unit(1)), ShippedInstructions.Store);

        var conclusion = Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral,
            MetaBag.Empty
                .With(HindsightAgent.NoteIdsKey, (IReadOnlyList<string>)["a", "b"])
                .With(HindsightAgent.EchoDepthKey, 2)) with { Generation = 4 };

        await agent.HandleAsync(conclusion, CancellationToken.None);

        var note = Assert.Single(passages.Passages);
        Assert.Equal(["a", "b"], note.ParentIds);
        Assert.Equal(3, note.EchoDepth);
        Assert.Equal(4, note.Generation);
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
            new StubSubstrate(_ => Task.FromResult(new SubstrateResult("mood|dull\nthought|person/family|note", TimeSpan.Zero, 5, 0m))),
            Manifest("Reflection", "slow-medium"), Options.Create(new ReflectionOptions { BatchSize = 1 }),
            passages, new StubEmbeddings(), ShippedInstructions.Store);

        await agent.HandleAsync(Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral, MetaBag.Empty), CancellationToken.None);

        Assert.Empty(passages.Passages);
    }
}
