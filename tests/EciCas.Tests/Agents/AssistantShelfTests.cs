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
/// Facts the person states about the persona, written the same way every
/// other fact is written: nothing in any instruction file asks for them.
///
/// Archivist already fills a subject field, and is already told the speaker
/// takes subject=user. A fact left on "assistant" is therefore a fact about
/// the persona, and reading that is the whole routing rule. The shelf is a
/// SCOPE, decided before anything is ranked — see AssistantScope for the
/// measurement that rules out making it a thirty-third category.
/// </summary>
public class AssistantShelfTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private const string TopicMarker = "folders inside";

    /// <summary>
    /// Answers the topic call, and throws on the category call. The claim
    /// under test is that a self fact never asks which drawer it goes in.
    /// </summary>
    private static ISubstrateProvider TopicOnly(string topic) =>
        new StubSubstrate(prompt => prompt.Contains(TopicMarker, StringComparison.Ordinal)
            ? Task.FromResult(new SubstrateResult(topic, TimeSpan.Zero, 5, 0m))
            : throw new InvalidOperationException("A fact about the persona was ranked against the vocabulary."));

    private static readonly List<string> Prompts = [];

    private static ISubstrateProvider Recording(string topic) =>
        new StubSubstrate(prompt =>
        {
            lock (Prompts) { Prompts.Add(prompt); }
            return Task.FromResult(new SubstrateResult(topic, TimeSpan.Zero, 5, 0m));
        });

    private static CatalogerAgent Agent(IMessageBus bus, BusActivityTracker activity, IArchiveStore store, ISubstrateProvider substrate) =>
        new(bus, activity, NullLogger<CatalogerAgent>.Instance, store, substrate,
            Options.Create(new SubstrateOptions { Agents = { ["Cataloger"] = new SubstrateAgentEntry() } }),
            Options.Create(new CatalogerOptions { BatchSize = 1 }), ShippedInstructions.Store);

    private static ArchiveRecord Fact(string subject, string key, string value) =>
        new(string.Empty, string.Empty, "self", subject, key, value, DateTimeOffset.UtcNow);

    private static Envelope Facts(string text, params ArchiveRecord[] facts) =>
        Envelope.Create(Topics.Facts, "Archivist", Severity.Neutral,
            MetaBag.Empty
                .With(ArchivistAgent.FactsKey, (IReadOnlyList<ArchiveRecord>)facts)
                .With(PerceptionAgent.TextKey, text)
                .With(PerceptionAgent.ProfileKey, "daniel"));

    /// <summary>
    /// The reported symptom: told about itself, the persona stored nothing.
    /// It stores it now, on its own shelf, without a category call.
    /// </summary>
    [Theory]
    [InlineData("assistant")]
    [InlineData("you")]
    [InlineData("yourself")]
    public async Task AFactAboutThePersona_LandsOnItsOwnShelf(string subject)
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, TopicOnly(AssistantScope.System))
            .HandleAsync(Facts("you run on version 0.1", Fact(subject, "version", "0.1")), CancellationToken.None);

        Assert.Equal(new ArchivePair(AssistantScope.Name, AssistantScope.System), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// Its drawers are its own three, not the user-domain vocabulary. A
    /// vocabulary topic offered to a self fact would be a drawer the shelf
    /// does not have.
    /// </summary>
    [Fact]
    public async Task TheTopicIsPickedFromTheShelfsOwnDrawers()
    {
        lock (Prompts) { Prompts.Clear(); }
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, Recording(AssistantScope.Persona))
            .HandleAsync(Facts("you are patient", Fact("assistant", "temperament", "patient")), CancellationToken.None);

        var topicPrompt = Assert.Single(Prompts, p => p.Contains(TopicMarker, StringComparison.Ordinal));
        foreach (var drawer in AssistantScope.Topics)
        {
            Assert.Contains(drawer, topicPrompt, StringComparison.Ordinal);
        }

        Assert.Contains(ClosedVocabulary.OtherTopic, topicPrompt, StringComparison.Ordinal);
        Assert.Equal(new ArchivePair(AssistantScope.Name, AssistantScope.Persona), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// An unlisted answer is "other" here too — the shelf gets the same valve
    /// every category has, and Librarian opens assistant/other in code.
    /// </summary>
    [Fact]
    public async Task AnInventedDrawerBecomesOther()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, TopicOnly("temperament"))
            .HandleAsync(Facts("you are patient", Fact("assistant", "temperament", "patient")), CancellationToken.None);

        Assert.Equal(new ArchivePair(AssistantScope.Name, ClosedVocabulary.OtherTopic), Assert.Single(store.IndexFor(null)));
    }

    /// <summary>
    /// The one carve-out inside the carve-out. The persona's name is per
    /// profile and `assistant` is a shared category, so a name filed on the
    /// shelf would be one name for everybody on the device. It is taken
    /// before the shelf is, and with no model call at all.
    /// </summary>
    [Fact]
    public async Task ARename_StillWinsOverTheShelf()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store,
                new StubSubstrate(_ => throw new InvalidOperationException("A rename asked a model where it goes.")))
            .HandleAsync(Facts("your name is Aria", Fact("assistant", "name", "Aria")), CancellationToken.None);

        Assert.Equal(PersonaName.Pair, Assert.Single(store.IndexFor("daniel")));
    }

    /// <summary>
    /// A person's fact is untouched by any of this: same key, same shape,
    /// different subject, and it goes through both calls as before.
    /// </summary>
    [Fact]
    public async Task APersonsFact_StillGoesThroughTheVocabulary()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var store = new InMemoryArchiveStore();

        await Agent(bus, activity, store, new StubSubstrate(prompt => Task.FromResult(new SubstrateResult(
                prompt.Contains(TopicMarker, StringComparison.Ordinal) ? "name" : "identity", TimeSpan.Zero, 5, 0m))))
            .HandleAsync(Facts("my name is Daniel", Fact("user", "name", "Daniel")), CancellationToken.None);

        Assert.Equal(new ArchivePair("identity", "name"), Assert.Single(store.IndexFor(null)));
    }
}
