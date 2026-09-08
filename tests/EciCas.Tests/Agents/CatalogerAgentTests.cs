using EciCas.Agents.Archivist;
using EciCas.Agents.Cataloger;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class CatalogerAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Cataloger"] = new AgentSubstrateEntry { Class = "slow-low" } } });

    /// <summary>
    /// Two calls per fact, and only the second one is shown a folder list —
    /// which is how a stub tells them apart without knowing the prompt text.
    /// </summary>
    private static ISubstrateProvider Answers(string category, string topic) =>
        new StubSubstrate(prompt => Task.FromResult(new SubstrateResult(
            prompt.Contains("folders inside", StringComparison.Ordinal) ? topic : category, TimeSpan.Zero, 5, 0m)));

    private static CatalogerAgent Agent(IMessageBus bus, BusActivityTracker activity, IArchiveStore store,
        ISubstrateProvider substrate, int batchSize = 1) =>
        new(bus, activity, NullLogger<CatalogerAgent>.Instance, store, substrate, Manifest(),
            Options.Create(new CatalogerOptions { BatchSize = batchSize }), ShippedInstructions.Store);

    private static ArchiveRecord Unfiled(string subject = "user", string key = "name", string value = "daniel") =>
        new(string.Empty, string.Empty, "self", subject, key, value, DateTimeOffset.UtcNow);

    private static Envelope Facts(string text, IReadOnlyList<ArchiveRecord> facts, string? profileId = null)
    {
        var meta = MetaBag.Empty.With(ArchivistAgent.FactsKey, facts).With(PerceptionAgent.TextKey, text);
        if (profileId is not null)
        {
            meta = meta.With(PerceptionAgent.ProfileKey, profileId);
        }

        return Envelope.Create(Topics.Facts, "Archivist", Severity.Neutral, meta);
    }

    [Fact]
    public async Task FilesTheFactAtThePairTheTwoCallsChose()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Answers("identity", "name"))
            .HandleAsync(Facts("my name is daniel", [Unfiled()]), CancellationToken.None);

        var records = await store.LookupAsync(new ArchivePair("identity", "name"), null, CancellationToken.None);
        var record = Assert.Single(records);
        Assert.Equal("daniel", record.Value);
        Assert.Equal("self", record.Subtopic);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ArchivistAgent.WrittenKind, written!.Meta.Get<string>(ArchivistAgent.ControlKindKey));
        Assert.Single(written.Meta.Get<IReadOnlyList<string>>(ArchivistAgent.WrittenRecordsKey)!);
    }

    /// <summary>
    /// The point of the closed list: a name nobody offered is a parquet file
    /// nobody ever opens, so an unlisted answer becomes "other" rather than a
    /// new folder. The drawer was already decided by then, and a fact in the
    /// right drawer is still reachable — Librarian opens category/other in
    /// code whenever it opens that category.
    /// </summary>
    [Fact]
    public async Task AnInventedTopicBecomesOther()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Answers("upkeep", "conservatory"))
            .HandleAsync(Facts("the conservatory leaks", [Unfiled("house", "conservatory", "leaks")]), CancellationToken.None);

        Assert.Equal(new ArchivePair("upkeep", "other"), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// Topic lists hold "name" and "nationality", and a containment test would
    /// read the first out of the second — filing a nationality under name.
    /// </summary>
    [Fact]
    public async Task ATopicIsMatchedAsAWholeWord()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Answers("identity", "nationality"))
            .HandleAsync(Facts("I am norwegian", [Unfiled("user", "nationality", "norwegian")]), CancellationToken.None);

        Assert.Equal(new ArchivePair("identity", "nationality"), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// A 4B answers "identity" about as often as it answers a sentence with
    /// "identity" somewhere in it.
    /// </summary>
    [Fact]
    public async Task TheCategoryIsFoundInsideASentence()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Answers("The drawer is identity.", "The folder is name."))
            .HandleAsync(Facts("my name is daniel", [Unfiled()]), CancellationToken.None);

        Assert.Equal(new ArchivePair("identity", "name"), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// No drawer, no address, and no invented one either: the file name is the
    /// whole index in this store, so a guess would be a file nobody opens.
    /// </summary>
    [Fact]
    public async Task AFactWithNoCategoryIsDroppedRatherThanGuessedAt()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Answers("nonsense", "name"))
            .HandleAsync(Facts("...", [Unfiled()]), CancellationToken.None);

        Assert.Empty(store.IndexFor(null));
        Assert.False(control.TryRead(out _));
    }

    /// <summary>
    /// A failed topic call still has a drawer. Losing the fact over the
    /// cheaper of the two decisions would be the worse trade.
    /// </summary>
    [Fact]
    public async Task WhenTheTopicCallFails_TheFactStillLandsInItsDrawer()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var substrate = new StubSubstrate(prompt => prompt.Contains("folders inside", StringComparison.Ordinal)
            ? throw new InvalidOperationException("down")
            : Task.FromResult(new SubstrateResult("identity", TimeSpan.Zero, 5, 0m)));

        await Agent(bus, activity, store, substrate)
            .HandleAsync(Facts("my name is daniel", [Unfiled()]), CancellationToken.None);

        Assert.Equal(new ArchivePair("identity", "other"), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>Turns, not facts: a lone just-stated name must not wait for other facts to arrive.</summary>
    [Fact]
    public async Task FlushesEveryBatchSizeTurns_IncludingTheEmptyOnes()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var store = new InMemoryArchiveStore();
        var agent = Agent(bus, activity, store, Answers("identity", "name"), batchSize: 2);

        await agent.HandleAsync(Facts("my name is daniel", [Unfiled()]), CancellationToken.None);
        Assert.False(control.TryRead(out _));

        await agent.HandleAsync(Facts("hello there", []), CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Single(written!.Meta.Get<IReadOnlyList<string>>(ArchivistAgent.WrittenRecordsKey)!);
    }

    /// <summary>
    /// A batch spans turns and speakers, so the profile is kept per record
    /// rather than read off whichever envelope happens to trigger the flush.
    /// </summary>
    [Fact]
    public async Task BatchedFactsKeepTheProfileThatStatedThem()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();
        var agent = Agent(bus, activity, store, Answers("identity", "name"), batchSize: 2);

        foreach (var profileId in new[] { "daniel", "ada" })
        {
            await agent.HandleAsync(Facts("a turn", [Unfiled()], profileId), CancellationToken.None);
        }

        Assert.Equal(["ada", "daniel"], store.Scoped.Select(r => r.ProfileId).Order());
    }

    /// <summary>
    /// Every category needs somewhere to put a fact no folder fits, or the
    /// fact is dropped instead — and the parser is the only place that can
    /// notice a hand edit removing one.
    /// </summary>
    [Fact]
    public void ShippedVocabulary_GivesEveryCategoryAnOtherTopic()
    {
        var vocabulary = ClosedVocabulary.Parse(ShippedInstructions.Store.For("Cataloger", "vocabulary"));

        Assert.Equal(32, vocabulary.Categories.Count);
        Assert.All(vocabulary.Categories, c => Assert.Contains("other", vocabulary.TopicsIn(c)));

        // Exactly 16 a drawer, and exact rather than a range because the v512
        // shelf is generated: build_v512.py asserts the shape before writing
        // and wire_v512.py copies it here, so a category with 15 means the
        // generator was bypassed by a hand edit, which is the failure worth
        // catching. Fifteen folders plus the "other" valve.
        Assert.All(vocabulary.Categories, c => Assert.Equal(16, vocabulary.TopicsIn(c).Count));
    }

    /// <summary>
    /// Every folder the selector can be shown has words to be shown with.
    ///
    /// The gloss is what took select from 41% to 53% and answer from 30% to
    /// 43%, and it does that one option line at a time -- a pair with no line
    /// here is simply offered bare, which costs that folder its share of the
    /// win and costs nothing else. Silent, in other words, and worth a test
    /// for the same reason the "other" check is: a hand edit adding a topic
    /// to the vocabulary is exactly when this gets forgotten.
    ///
    /// "other" is excluded on both sides. Librarian never shows it, so a
    /// gloss for it would be words nobody reads.
    /// </summary>
    [Fact]
    public void ShippedGloss_DefinesEveryFolderTheSelectorIsShown()
    {
        var vocabulary = ClosedVocabulary.Parse(ShippedInstructions.Store.For("Cataloger", "vocabulary"));
        var gloss = TopicGloss.Parse(ShippedInstructions.Store.For("Cataloger", "gloss"));

        var undefined = vocabulary.Categories
            .SelectMany(c => vocabulary.TopicsIn(c).Select(t => (Category: c, Topic: t)))
            .Where(p => !string.Equals(p.Topic, "other", StringComparison.OrdinalIgnoreCase))
            .Where(p => gloss.For(p.Category, p.Topic) is null)
            .Select(p => p.Category + "/" + p.Topic)
            .ToList();

        Assert.Empty(undefined);

        // And nothing glossed that is not a folder -- a renamed topic leaves
        // its old line behind, which reads as a definition of nothing.
        var pairs = vocabulary.Categories
            .SelectMany(c => vocabulary.TopicsIn(c).Select(t => c + "/" + t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(pairs.Count - vocabulary.Categories.Count, gloss.Count);
    }

    /// <summary>
    /// The "category" section describes ten drawers in prose and the
    /// "vocabulary" section lists them; only the second one is parsed. A hand
    /// edit that renames a drawer in the prose leaves the model answering a
    /// name MatchCategory has never heard of, and a fact with no category is
    /// dropped rather than guessed at — silently, one fact at a time. The two
    /// sections are in the same file precisely so they can be checked against
    /// each other, which is what this does.
    /// </summary>
    [Fact]
    public void ShippedScopeLines_DescribeExactlyTheCategoriesTheParserKnows()
    {
        var vocabulary = ClosedVocabulary.Parse(ShippedInstructions.Store.For("Cataloger", "vocabulary"));
        var section = ShippedInstructions.Store.For("Cataloger", "category");

        // A scope line is "name" followed by the gap that lines the prose up;
        // continuations are indented past it and are not names.
        var described = section.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^(\w+) {2,}\S"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        Assert.Equal(vocabulary.Categories.ToHashSet(), described);
    }
}
