using EciCas.Agents.Archivist;
using EciCas.Agents.Utterances;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// Archivist extracts and publishes; it no longer writes. What it produces is
/// a fact with no address on it — CatalogerAgentTests covers the other half.
/// </summary>
public class ArchivistAgentTests
{
    private sealed class StubSubstrate(Func<string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken) => respond(prompt);
    }

    private static IOptions<SubstrateOptions> Manifest() =>
        Options.Create(new SubstrateOptions { Agents = { ["Archivist"] = new SubstrateAgentEntry() } });

    private const string FactLine = "subtopic=son subject=marcus holth key=birthdate value=2020-08-28";

    private static ArchivistAgent Agent(IMessageBus bus, BusActivityTracker activity, ISubstrateProvider substrate) =>
        new(bus, activity, NullLogger<ArchivistAgent>.Instance, substrate, Manifest(), ShippedInstructions.Store);

    private static Envelope Bundle(string text, MetaBag? extra = null) =>
        Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            (extra ?? MetaBag.Empty).With(PerceptionAgent.TextKey, text));

    private static IReadOnlyList<ArchiveRecord> FactsOn(Envelope envelope) =>
        envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(ArchivistAgent.FactsKey) ?? [];

    [Fact]
    public async Task PublishesTheExtractedFact_WithScoredImportance_AndNoAddress()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("our son's birthday was yesterday"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        var record = Assert.Single(FactsOn(published!));
        Assert.Equal("marcus holth", record.Subject);
        Assert.Equal(0.6, record.Importance);

        // The address is Cataloger's to fill, and leaving it blank is what
        // makes a fact that never reached Cataloger impossible to write.
        Assert.Equal("", record.Category);
        Assert.Equal("", record.Topic);
    }

    /// <summary>
    /// Published even when empty: Cataloger's write batch counts turns, and a
    /// turn that never arrives holds the previous turn's fact off disk.
    /// </summary>
    [Fact]
    public async Task WhenNothingExplicitlyStated_StillPublishes_ButWithNoFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("tell me about your system"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Empty(FactsOn(published!));
    }

    [Fact]
    public async Task SentenceIsCarriedOnTheRecord_AndRunsToTheEndOfTheLine()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var line = FactLine + " sentence=Marcus Holth was born on 28 August 2020.";
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(line, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("our son's birthday was yesterday"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        var record = Assert.Single(FactsOn(published!));

        // The whole sentence, spaces and full stop included: it is the last
        // field precisely so the marker split has nothing to cut it at.
        Assert.Equal("Marcus Holth was born on 28 August 2020.", record.Sentence);
        Assert.Equal("2020-08-28", record.Value);
    }

    /// <summary>
    /// The field is optional on purpose. A model that omits it costs the row
    /// some retrieval surface; treating it as required would cost the fact.
    /// </summary>
    [Fact]
    public async Task WhenNoSentenceIsWritten_TheFactStillLands_AndRendersFromItsAddress()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("our son's birthday was yesterday"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        var record = Assert.Single(FactsOn(published!));
        Assert.Equal("", record.Sentence);
        Assert.Equal("son / marcus holth birthdate = 2020-08-28", record.Rendered);
    }

    [Fact]
    public async Task WhenSubstrateCallFails_PublishesNoFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => throw new InvalidOperationException("down"));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("turn one"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Empty(FactsOn(published!));
    }

    [Fact]
    public async Task MissingSubtopic_ParsesInsteadOfBeingLost()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "subject=maia key=nickname value=benita", TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("maia calls herself benita at school"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        var record = Assert.Single(FactsOn(published!));
        Assert.Equal("general", record.Subtopic);
        Assert.Equal("benita", record.Value);
    }

    /// <summary>
    /// Subtopic leads a well-formed line and is also the field the model drops
    /// most often, so two facts can arrive with only one subtopic marker
    /// between them. Splitting on subtopic alone would glue them into one row.
    /// </summary>
    [Fact]
    public async Task TwoFactsSharingOneSubtopicMarkerAreTwoFacts()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(
            "subtopic=son subject=marcus key=age value=6\nsubject=maia key=age value=9", TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("marcus is 6 and maia is 9"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Equal(["marcus", "maia"], FactsOn(published!).Select(r => r.Subject));
    }

    /// <summary>
    /// A bare "test" came back as action/user/test = none -- the requested
    /// shape filled in with no fact in it. The words that mean empty are
    /// rejected like empty, and a real value beside them survives.
    /// </summary>
    [Theory]
    [InlineData("subtopic=action subject=user key=test value=none", false)]
    [InlineData("subtopic=action subject=user key=test value=None.", false)]
    [InlineData("subtopic=y subject=user key=k value=nothing", false)]
    [InlineData("subtopic=y subject=user key=k value=unknown", false)]
    [InlineData("subtopic=y subject=user key=k value=-", false)]
    [InlineData("subtopic=y subject=user key=k value=none of your business", true)]
    [InlineData("subtopic=y subject=user key=k value=oslo", true)]
    public async Task AValueThatMeansEmptyIsNotAFact(string line, bool extracted)
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(line, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("test"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Equal(extracted, FactsOn(published!).Count > 0);
    }

    [Fact]
    public async Task WhenTriggeredBySelf_HardSkips_NeverCallsSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var called = false;
        var substrate = new StubSubstrate(_ => { called = true; return Task.FromResult(new SubstrateResult(FactLine, TimeSpan.Zero, 10, 0m)); });

        await Agent(bus, activity, substrate).HandleAsync(
            Bundle("whether the trip dates still work", MetaBag.Empty.With(ReflectionAgent.TriggeredByKey, "self")),
            CancellationToken.None);

        Assert.False(facts.TryRead(out _));
        Assert.False(called);
    }

    /// <summary>
    /// The bundle carries both Librarian's selected pairs and the rows Recall
    /// actually read, and neither belongs in an extraction prompt. The pairs
    /// used to be shown, to bias the model toward reusing an existing address
    /// — that job is Cataloger's closed vocabulary now, and a recalled value
    /// would let an already-archived fact be re-extracted as a freshly stated
    /// one, which the write-time merge would then hide by overwriting the row
    /// it came from. Load-bearing by omission until this test, since "give
    /// Archivist more context" is a one-line change that closes the loop.
    /// </summary>
    [Fact]
    public async Task ExtractionPromptCarriesNeitherSelectedPairsNorRecalledValues()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var prompt = "";
        var substrate = new StubSubstrate(p =>
        {
            prompt = p;
            return Task.FromResult(new SubstrateResult("", TimeSpan.Zero, 10, 0m));
        });

        // Values deliberately unlike anything in the instruction file's own
        // worked examples, so a hit is the recalled row and nothing else.
        var recalled = new ArchiveRecord("person", "family", "daughter", "vera lind", "birthplace",
            "tromso", DateTimeOffset.UtcNow, ArchiveDomain.External, 0.9);

        await Agent(bus, activity, substrate).HandleAsync(Bundle("how old is he now", MetaBag.Empty
                .With("librarian.selected-pairs", (IReadOnlyList<ArchivePair>)[new ArchivePair("person", "family")])
                .With(ConsultAgent.RecalledFactsKey, (IReadOnlyList<ArchiveRecord>)[recalled])),
            CancellationToken.None);

        Assert.DoesNotContain("person/family", prompt);
        Assert.DoesNotContain("tromso", prompt);
        Assert.DoesNotContain("vera lind", prompt);
    }

    /// <summary>
    /// Every literal example in the instruction must be about the sentinel
    /// subject, so that a copied one is obvious in the archive.
    ///
    /// The instruction once carried four worked examples and a real
    /// substrate copied the first back on a turn that stated nothing —
    /// "What is my name?" — filing it every turn after. It hid because the
    /// example used the developer's own name: a copied example and a
    /// correct extraction were the same string.
    ///
    /// Deleting the examples fixed that and cost something else. Without
    /// one, the model started restating the value in the subject slot.
    /// Examples teach the discipline, not just the format, so they are back
    /// — and the way to keep the old bug from coming back with them is to
    /// make every example unfalsifiable as an extraction. No turn will ever
    /// state a fact about Lisbon's rainfall, so such a row can only have
    /// been copied.
    /// </summary>
    [Fact]
    public void ShippedInstruction_ExamplesAreAboutNothingTheTurnCouldBeAbout()
    {
        var lines = ShippedExampleLines().ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("subject=lisbon", l, StringComparison.Ordinal));
    }

    /// <summary>
    /// The example lines are well-formed facts sitting in the prompt, and on
    /// a message stating nothing the model copies one out: measured at
    /// roughly half of greetings and questions on the 4B. Taken from the
    /// shipped file rather than written out here, so an edit to the examples
    /// cannot leave this passing against a line the prompt no longer holds.
    /// </summary>
    [Fact]
    public async Task AnExampleCopiedBackOutOfThePromptIsNotAFact()
    {
        var example = ShippedExampleLines().First();

        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(example, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("hello there"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Empty(FactsOn(published!));
    }

    /// <summary>
    /// The filter matches the whole row, not the address: a turn that really
    /// does state something about the example's subject still gets extracted.
    /// </summary>
    [Fact]
    public async Task TheSameAddressWithADifferentValueIsStillAFact()
    {
        var altered = ShippedExampleLines().First();
        altered = altered[..altered.LastIndexOf("value=", StringComparison.Ordinal)] + "value=812mm";

        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var facts = bus.Subscribe(Topics.Facts);
        var substrate = new StubSubstrate(_ => Task.FromResult(new SubstrateResult(altered, TimeSpan.Zero, 10, 0m)));

        await Agent(bus, activity, substrate).HandleAsync(Bundle("Lisbon gets 812mm of rain"), CancellationToken.None);

        Assert.True(facts.TryRead(out var published));
        Assert.Equal("812mm", Assert.Single(FactsOn(published!)).Value);
    }

    /// <summary>Literal example rows in the shipped prompt: templated ones carry "&lt;" and cannot be copied out as facts.</summary>
    private static IEnumerable<string> ShippedExampleLines() =>
        ShippedInstructions.Store.For("Archivist")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("subtopic=", StringComparison.Ordinal) && !l.Contains('<'));
}
