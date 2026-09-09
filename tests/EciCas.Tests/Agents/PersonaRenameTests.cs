using EciCas.Agents.Archivist;
using EciCas.Agents.Cataloger;
using EciCas.Agents.Identity;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The rename WRITE path, end to end through the agent that actually holds
/// the pen.
///
/// IdentityAgentTests.Rename_IsVisibleOnTheNextTurn writes the row with
/// archive.WriteAsync, which proves the read and the cache invalidation and
/// nothing else — the rename was unreachable from conversation for as long
/// as that was the only test, because `persona` is not in the closed
/// vocabulary and Cataloger cannot emit a category that is not in it. So
/// every test here starts at the facts envelope Archivist publishes and ends
/// at PersonaName.ForAsync, with no direct store write anywhere between.
/// </summary>
public class PersonaRenameTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    /// <summary>
    /// A filing model that always answers identity/name — where a rename
    /// used to land. If the deterministic path ever stops running, the row
    /// goes there and the assertions here fail with the real symptom rather
    /// than a stub error.
    /// </summary>
    private static ISubstrateProvider FilesEverythingUnderIdentityName() =>
        new StubSubstrate(prompt => Task.FromResult(new SubstrateResult(
            prompt.Contains("folders inside", StringComparison.Ordinal) ? "name" : "identity", TimeSpan.Zero, 5, 0m)));

    private static ISubstrateProvider NeverCalled() =>
        new StubSubstrate(_ => throw new InvalidOperationException("A reserved address was ranked against the vocabulary."));

    private static IOptions<SubstrateOptions> Manifest(bool useSubstrate = true) =>
        Options.Create(new SubstrateOptions
        {
            Agents = { ["Cataloger"] = new SubstrateAgentEntry { UseSubstrate = useSubstrate } },
        });

    private static CatalogerAgent Agent(IMessageBus bus, BusActivityTracker activity, IArchiveStore store,
        ISubstrateProvider substrate, bool useSubstrate = true) =>
        new(bus, activity, NullLogger<CatalogerAgent>.Instance, store, substrate, Manifest(useSubstrate),
            Options.Create(new CatalogerOptions { BatchSize = 1 }), ShippedInstructions.Store);

    /// <summary>Exactly the shape Archivist emits: no category, no topic.</summary>
    private static ArchiveRecord Fact(string subject, string key, string value) =>
        new(string.Empty, string.Empty, "name", subject, key, value, DateTimeOffset.UtcNow);

    private static Envelope Facts(string text, string? profileId, params ArchiveRecord[] facts) =>
        Envelope.Create(Topics.Facts, "Archivist", Severity.Neutral,
            MetaBag.Empty
                .With(ArchivistAgent.FactsKey, (IReadOnlyList<ArchiveRecord>)facts)
                .With(PerceptionAgent.TextKey, text)
                .With(PerceptionAgent.ProfileKey, profileId ?? string.Empty));

    private static Task<string> NameFor(IArchiveStore store, string? profileId) =>
        new PersonaName(store, Options.Create(new PersonaNameOptions())).ForAsync(profileId, CancellationToken.None);

    /// <summary>
    /// The whole point: a rename stated in conversation is readable as the
    /// persona's name afterwards, without anyone writing the row by hand.
    /// </summary>
    [Fact]
    public async Task ARenameStatedInConversation_IsTheNameAfterwards()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        Assert.Equal("Morrow", await NameFor(store, "daniel"));

        await Agent(bus, activity, store, FilesEverythingUnderIdentityName())
            .HandleAsync(Facts("from now on your name is Aria", "daniel", Fact("assistant", "name", "Aria")), CancellationToken.None);

        Assert.Equal("Aria", await NameFor(store, "daniel"));
    }

    /// <summary>
    /// It lands at the address IdentityAgent and GET /api/persona read, not
    /// merely somewhere that happens to contain the word.
    /// </summary>
    [Fact]
    public async Task ItLandsAtPersonaName_NotUnderTheVocabulary()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, FilesEverythingUnderIdentityName())
            .HandleAsync(Facts("call yourself Sol", "daniel", Fact("you", "your name", "Sol")), CancellationToken.None);

        var rows = await store.LookupAsync(PersonaName.Pair, "daniel", CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(PersonaName.Subject, row.Subject);
        Assert.Equal(PersonaName.NameKey, row.Key);
        Assert.Equal("Sol", row.Value);

        Assert.Empty(await store.LookupAsync(new ArchivePair("identity", "name"), "daniel", CancellationToken.None));
    }

    /// <summary>
    /// No substrate call is made for it. This is the fix's actual claim:
    /// `persona` is a scope decided before ranking, so it is never ranked.
    /// </summary>
    [Fact]
    public async Task TheRenameIsNeverRankedAgainstTheVocabulary()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, NeverCalled())
            .HandleAsync(Facts("your name is Aria", "daniel", Fact("assistant", "name", "Aria")), CancellationToken.None);

        Assert.Equal("Aria", await NameFor(store, "daniel"));
    }

    /// <summary>
    /// And it still works with no filing model configured at all, which is
    /// what putting it outside the UseSubstrate switch buys.
    /// </summary>
    [Fact]
    public async Task WithNoFilingModel_TheRenameStillLands()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, NeverCalled(), useSubstrate: false)
            .HandleAsync(Facts("your name is Aria", "daniel", Fact("assistant", "name", "Aria")), CancellationToken.None);

        Assert.Equal("Aria", await NameFor(store, "daniel"));
    }

    /// <summary>
    /// The write carries the speaker's profile, which is what makes the
    /// rename theirs alone. Only half the promise is testable here — this
    /// fake looks a pair up across every profile — and the other half is
    /// IdentityAgentTests.PersonaCategory_IsNotShared, which pins that
    /// `persona` is absent from Archive:SharedCategories and so is filed
    /// per profile rather than per device.
    /// </summary>
    [Fact]
    public async Task TheRenameIsWrittenScopedToTheSpeaker()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, NeverCalled())
            .HandleAsync(Facts("your name is Aria", "daniel", Fact("assistant", "name", "Aria")), CancellationToken.None);

        var (profileId, record) = Assert.Single(store.Scoped);
        Assert.Equal("daniel", profileId);
        Assert.Equal(PersonaName.Pair, record.Pair);
    }

    /// <summary>
    /// The other direction, and the one that matters more: a fact about the
    /// person goes through the vocabulary like every other fact. Subject is
    /// the whole test — same key, same value shape.
    /// </summary>
    [Fact]
    public async Task APersonsOwnName_IsStillFiledByTheVocabulary()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, FilesEverythingUnderIdentityName())
            .HandleAsync(Facts("my name is Daniel", "daniel", Fact("user", "name", "Daniel")), CancellationToken.None);

        Assert.Equal("Morrow", await NameFor(store, "daniel"));
        Assert.Single(await store.LookupAsync(new ArchivePair("identity", "name"), "daniel", CancellationToken.None));
    }

    /// <summary>
    /// A key ending in the word "name" is the address; a single word that
    /// merely ends in those letters is not. The persona having a nickname is
    /// an ordinary fact about it, not a rename.
    /// </summary>
    [Theory]
    [InlineData("assistant", "name")]
    [InlineData("Assistant", "Name")]
    [InlineData("you", "your name")]
    [InlineData("yourself", "new name")]
    public void TheseRenameIt(string subject, string key) =>
        Assert.NotNull(PersonaName.Rename(Fact(subject, key, "Aria")));

    [Theory]
    [InlineData("user", "name")]
    [InlineData("daniel", "name")]
    [InlineData("assistant", "nickname")]
    [InlineData("assistant", "codename")]
    [InlineData("assistant", "name of author")]
    [InlineData("assistant", "colour")]
    public void TheseDoNot(string subject, string key) =>
        Assert.Null(PersonaName.Rename(Fact(subject, key, "Aria")));

    /// <summary>
    /// An empty value would read back as no name at all — ForAsync skips
    /// blank rows — so it must not displace the one that is there.
    /// </summary>
    [Fact]
    public void ABlankValue_RenamesNothing() =>
        Assert.Null(PersonaName.Rename(Fact("assistant", "name", "  ")));
}
