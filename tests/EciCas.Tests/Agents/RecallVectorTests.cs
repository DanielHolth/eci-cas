using EciCas.Agents.Librarian;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The row-vector read path: what a pair full of current vectors buys, and
/// every way a pair falls back to the path it had before vectors existed.
/// </summary>
public class RecallVectorTests
{
    private sealed class NeverCalledSubstrate : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The picking model was called on a turn that should have been answered by cosine alone.");
    }

    private sealed class RecordingSubstrate(Func<string, string> respond) : ISubstrateProvider
    {
        public List<string> Prompts { get; } = [];

        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
        {
            lock (Prompts)
            {
                Prompts.Add(prompt);
            }

            return Task.FromResult(new SubstrateResult(respond(prompt), TimeSpan.Zero, 5, 0m));
        }
    }

    private static IOptions<AgentSubstrateManifest> Manifest() =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Recall"] = new AgentSubstrateEntry { Class = "fast-low" } } });

    /// <summary>
    /// An embedder a test can predict: the vector is the count of "x"
    /// characters in the text, normalized. One dimension would make cosine
    /// degenerate — every positive value scores the same against every
    /// other — so it is two, the second fixed, which keeps the ordering
    /// monotonic in the first.
    ///
    /// Normalized because VectorMath.Cosine is a bare dot product: the real
    /// providers normalize at write time so the denominator is always 1, and
    /// a stub that skipped it would be measuring length, not direction.
    /// </summary>
    private static StubEmbeddings Counting() =>
        new(text => VectorMath.Normalize([text.Count(c => c == 'x'), 1f]));

    private static Envelope Selection(ArchivePair pair, string text, float[]? query = null)
    {
        var meta = MetaBag.Empty
            .With(LibrarianAgent.SelectedPairsKey, (IReadOnlyList<ArchivePair>)[pair])
            .With(PerceptionAgent.TextKey, text);

        if (query is not null)
        {
            meta = meta.With(LibrarianAgent.QueryVectorKey, query);
        }

        return Envelope.Create(Topics.SelectedPairs, "Librarian", Severity.Neutral, meta);
    }

    /// <summary>A row carrying a current vector over its own text.</summary>
    private static ArchiveRecord Embedded(string key, string value, StubEmbeddings embeddings)
    {
        var row = new ArchiveRecord("person", "family", "sub", "subject", key, value, DateTimeOffset.UtcNow);
        var vector = embeddings.EmbedAsync([row.EmbeddedText], EmbeddingKind.Passage, CancellationToken.None).Result[0];
        return row with
        {
            Embedding = vector,
            EmbeddingModelId = embeddings.ModelId,
            EmbeddingHash = ArchiveEmbedding.HashOf(row.EmbeddedText),
        };
    }

    private static RecallAgent Agent(IMessageBus bus, BusActivityTracker activity, IArchiveStore store,
        ISubstrateProvider substrate, IEmbeddingProvider embeddings, RecallOptions? options = null,
        int depth = 2) =>
        new(bus, activity, NullLogger<RecallAgent>.Instance, store, substrate, Manifest(),
            Options.Create(options ?? new RecallOptions { RecentRows = 0 }),
            ShippedInstructions.Store, new RuntimeKnobs { RecallDepth = depth }, embeddings);

    /// <summary>
    /// The point of the whole thing: a fully embedded pair hands Intent the
    /// rows nearest the question, and spends no picking call doing it.
    /// </summary>
    [Fact]
    public async Task AFullyEmbeddedPair_IsNarrowedByCosine_WithNoPickingCall()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var embeddings = Counting();
        var store = new InMemoryArchiveStore();

        await store.WriteAsync(
        [
            Embedded("far", "a", embeddings),
            Embedded("middle", "xx", embeddings),
            Embedded("near", "xxxxxx", embeddings),
        ], null, CancellationToken.None);

        var agent = Agent(bus, activity, store, new NeverCalledSubstrate(), embeddings);

        await agent.HandleAsync(Selection(new ArchivePair("person", "family"), "xxxxxxxx"), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!;
        Assert.Equal(2, facts.Count);
        Assert.Equal(["near", "middle"], facts.Select(f => f.Key));
    }

    /// <summary>
    /// One unembedded row is enough to send the whole pair back to the
    /// picking path. Sweeping half a file by cosine would rank the embedded
    /// half against nothing and lose the other half every time.
    /// </summary>
    [Fact]
    public async Task OneRowWithoutAVector_SendsTheWholePairBackToPicking()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        bus.Subscribe(Topics.Advisories);
        var embeddings = Counting();
        var store = new InMemoryArchiveStore();

        // More rows than RecallDepth, or the turn takes the pass-everything
        // shortcut and never reaches a picking call at all - which would make
        // this test pass for the wrong reason.
        await store.WriteAsync(
        [
            .. Enumerable.Range(0, 12).Select(i => Embedded($"row{i}", new string('x', i), embeddings)),
            new ArchiveRecord("person", "family", "sub", "subject", "bare", "no vector here", DateTimeOffset.UtcNow),
        ], null, CancellationToken.None);

        var substrate = new RecordingSubstrate(_ => "0");
        var agent = Agent(bus, activity, store, substrate, embeddings,
            new RecallOptions { RecentRows = 0, RowsPerWorker = 50, MaxPickedPerWorker = 1 }, depth: 1);

        await agent.HandleAsync(Selection(new ArchivePair("person", "family"), "xxxxxxxx"), CancellationToken.None);

        Assert.Single(substrate.Prompts);
        Assert.Contains("bare", substrate.Prompts[0]);
    }

    /// <summary>
    /// A row restated at its own address inherits the old vector, and the
    /// hash is what stops that vector being trusted: an "Oslo" that became
    /// "Bergen" would otherwise go on scoring as Oslo forever.
    /// </summary>
    [Fact]
    public void ARestatedRow_InvalidatesItsOwnVector()
    {
        var embeddings = Counting();
        var oslo = Embedded("home", "oslo", embeddings);

        Assert.True(oslo.HasVector(embeddings.ModelId));

        // Exactly what the store's merge does: same address, new value, and
        // everything else — the vector included — carried over.
        var bergen = oslo with { Value = "bergen" };

        Assert.False(bergen.HasVector(embeddings.ModelId));
    }

    /// <summary>A vector from another embedder is not comparable and does not count.</summary>
    [Fact]
    public void AVectorFromAnotherModel_DoesNotCount()
    {
        var row = Embedded("home", "oslo", Counting());

        Assert.False(row.HasVector("some-other-embedder"));
        Assert.False(row.HasVector(""));
    }

    /// <summary>
    /// No embedder is the offline tier, not a fault: the pre-vector path runs
    /// and the rows still reach Intent.
    /// </summary>
    [Fact]
    public async Task WithNoEmbedder_TheReadPathIsExactlyWhatItWasBefore()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new InMemoryArchiveStore();
        await store.WriteAsync([Embedded("home", "oslo", Counting())], null, CancellationToken.None);

        var agent = Agent(bus, activity, store, new NeverCalledSubstrate(), new StubEmbeddings());

        await agent.HandleAsync(Selection(new ArchivePair("person", "family"), "where do I live"), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Single(advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!);
    }

    /// <summary>
    /// Librarian's vector is used as published rather than re-derived. The
    /// published vector here contradicts the turn text, so which row comes
    /// back says which of the two the ranking actually read.
    /// </summary>
    [Fact]
    public async Task TheQueryVectorFromLibrarian_IsWhatRanks()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var embeddings = Counting();
        var store = new InMemoryArchiveStore();

        await store.WriteAsync(
        [
            Embedded("few", "x", embeddings),
            Embedded("many", "xxxxxxxxxx", embeddings),
        ], null, CancellationToken.None);

        var agent = Agent(bus, activity, store, new NeverCalledSubstrate(), embeddings,
            new RecallOptions { RecentRows = 0 }, depth: 1);

        await agent.HandleAsync(
            Selection(new ArchivePair("person", "family"), "xxxxxxxxxxxx", query: [0.05f, 1f]),
            CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!;
        Assert.Equal("few", Assert.Single(facts).Key);
    }

    /// <summary>
    /// The recency lane is a set of candidates like any other. It used to
    /// reach Intent on its timestamp order alone — the newest rows, whatever
    /// the turn was about — so a burst of unrelated writes arrived as facts.
    /// Recency is how the lane is built, not how it is ranked.
    /// </summary>
    [Fact]
    public async Task TheRecencyLane_IsRankedByCosine_NotByClock()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var embeddings = Counting();
        var store = new InMemoryArchiveStore();

        // Write order is the fake's clock: "far" is the newest thing in the
        // archive and the worst answer to the question.
        await store.WriteAsync([Embedded("near", "xxxxxxxx", embeddings)], null, CancellationToken.None);
        await store.WriteAsync([Embedded("far", "a", embeddings)], null, CancellationToken.None);

        var agent = Agent(bus, activity, store, new NeverCalledSubstrate(), embeddings,
            new RecallOptions { RecentRows = 5, PickAfterVector = false }, depth: 1);

        await agent.HandleAsync(Selection(new ArchivePair("person", "family"), "xxxxxxxx"), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var facts = advisory!.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)!;
        Assert.Equal("near", Assert.Single(facts).Key);
    }
}
