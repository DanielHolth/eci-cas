using EciCas.Agents.Utterances;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The two model calls that decide what the archive holds and what a read
/// hands on, with the model's reply stubbed.
/// </summary>
public class FactPickerTests
{
    private sealed class StubSubstrate(string reply, string? model = null) : ISubstrateProvider
    {
        public string? Prompt { get; private set; }

        public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.FromResult(new SubstrateResult(reply, TimeSpan.Zero, null, null, Model: model));
        }
    }

    private static async Task<IReadOnlyList<ExtractedFact>> Extract(string reply, string? model = null)
    {
        var extractor = new SubstrateFactExtractor(new StubSubstrate(reply, model),
            Options.Create(new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<SubstrateFactExtractor>.Instance);

        return await extractor.ExtractAsync(
            new Utterance("u", "said something", DateTimeOffset.UnixEpoch, "user"), null, CancellationToken.None);
    }

    private static readonly IReadOnlyList<Consulted> Shortlist =
    [
        Hit("Governance publishes events.bundle."),
        Hit("Susana's birthday is 10.02.2018."),
        Hit("I call Maria Benita Maia at home."),
    ];

    private static Consulted Hit(string text) =>
        new(new Fact(Guid.NewGuid().ToString("n"), 1, text, DateTimeOffset.UnixEpoch, "user", []), 0.8, true);

    private static Task<IReadOnlyList<Consulted>?> Pick(string reply, int max = 8) =>
        new FactPicker(new StubSubstrate(reply), NullLogger<FactPicker>.Instance)
            .PickAsync("when is Maia's birthday?", Shortlist, max, CancellationToken.None);

    [Fact]
    public async Task NumbersPickRowsInTheOrderGiven()
    {
        var picked = await Pick("3, 2");

        Assert.Equal(["I call Maria Benita Maia at home.", "Susana's birthday is 10.02.2018."],
            picked!.Select(p => p.Row.Text));
    }

    [Fact]
    public async Task OutOfRangeAndRepeatedNumbersAreIgnored_AndMaxHolds()
    {
        var picked = await Pick("2, 2, 9, 0, 3, 1", max: 2);

        Assert.Equal(2, picked!.Count);
        Assert.Equal("Susana's birthday is 10.02.2018.", picked[0].Row.Text);
    }

    [Fact]
    public async Task NoneMeansNothingHelps() => Assert.Empty((await Pick("NONE"))!);

    [Fact]
    public async Task ProseWithNoNumberIsAFailureNotAVerdict() => Assert.Null(await Pick("I think Maia is lovely."));

    [Fact]
    public async Task AQuestionStoresNoFact()
    {
        var extractor = new SubstrateFactExtractor(new StubSubstrate("NONE"),
            Options.Create(new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<SubstrateFactExtractor>.Instance);

        var facts = await extractor.ExtractAsync(
            new Utterance("u", "how many kids do i have?", DateTimeOffset.UnixEpoch, "user"), null, CancellationToken.None);

        Assert.Empty(facts);
    }

    [Fact]
    public async Task EvenAShortStatementIsSentToTheExtractor()
    {
        var substrate = new StubSubstrate("Maia is my daughter.");
        var extractor = new SubstrateFactExtractor(substrate,
            Options.Create(new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<SubstrateFactExtractor>.Instance);

        await extractor.ExtractAsync(new Utterance("u", "maia is my girl", DateTimeOffset.UnixEpoch, "user"), null, CancellationToken.None);

        Assert.NotNull(substrate.Prompt);
        Assert.DoesNotContain("JUST BEFORE", substrate.Prompt);
    }

    [Fact]
    public async Task ThePreviousReplyCanBeSwitchedOff()
    {
        var substrate = new StubSubstrate("NONE");
        var extractor = new SubstrateFactExtractor(substrate,
            Options.Create(new UtteranceOptions { ExtractorEnabled = true, ExtractorSeesPreviousReply = false }),
            NullLogger<SubstrateFactExtractor>.Instance);

        await extractor.ExtractAsync(new Utterance("u", "I totally agree.", DateTimeOffset.UnixEpoch, "user"),
            "The first knob is Tier.", CancellationToken.None);

        Assert.DoesNotContain("The first knob", substrate.Prompt);
    }

    [Fact]
    public async Task AWellFormedLine_FillsEveryColumn()
    {
        var facts = await Extract("Ingrid's birthday is 1988-03-04 | date | Ingrid | 1", "gpt-5.6-luna");

        var fact = Assert.Single(facts);
        Assert.Equal("Ingrid's birthday is 1988-03-04", fact.Text);
        Assert.Equal("date", fact.Class);
        Assert.Equal("Ingrid", fact.Entity);
        Assert.Equal(1, fact.Sensitivity);
        Assert.Equal("gpt-5.6-luna", fact.OriginModel);
    }

    /// <summary>
    /// The text is the only field that matters enough to fail over. A model
    /// that forgets the format still gets its sentence into the archive with
    /// the columns left null -- which is exactly the state that puts the row
    /// back in front of the next rebuild.
    /// </summary>
    [Fact]
    public async Task ALineWithNoFields_IsStillAFact()
    {
        var fact = Assert.Single(await Extract("Maia is my daughter.", "qwen3.5-2b"));

        Assert.Equal("Maia is my daughter.", fact.Text);
        Assert.Null(fact.Class);
        Assert.Null(fact.Entity);
        Assert.Null(fact.Sensitivity);
        Assert.Equal("qwen3.5-2b", fact.OriginModel);
    }

    /// <summary>
    /// Counting fields from the right is what keeps a sentence containing a
    /// pipe from being truncated at it. The metadata is always the last
    /// three; everything before is what was said.
    /// </summary>
    [Fact]
    public async Task APipeInsideTheSentence_DoesNotEatTheFact()
    {
        var fact = Assert.Single(await Extract("I use the | key in bash | skill | self | 0"));

        Assert.Equal("I use the | key in bash", fact.Text);
        Assert.Equal("skill", fact.Class);
        Assert.Equal(0, fact.Sensitivity);
    }

    /// <summary>A class nobody agreed to lands in the valve, not on the row.</summary>
    [Fact]
    public async Task AnInventedClass_BecomesOther()
    {
        var fact = Assert.Single(await Extract("I feel good about it | vibe | self | 0"));

        Assert.Equal(FactClasses.Other, fact.Class);
    }

    /// <summary>Sensitivity is an ordinal with three values, whatever a model writes.</summary>
    [Fact]
    public async Task SensitivityIsClampedToTheOrdinal()
    {
        Assert.Equal(2, Assert.Single(await Extract("x | state | self | 9")).Sensitivity);
        Assert.Equal(0, Assert.Single(await Extract("x | state | self | -3")).Sensitivity);
        Assert.Null(Assert.Single(await Extract("x | state | self | high")).Sensitivity);
    }

    /// <summary>
    /// A relative date is a reference like any pronoun, and the only thing
    /// that can resolve it is the day it was said. The prompt has to carry
    /// that day or the rule is unfollowable.
    /// </summary>
    [Fact]
    public void ThePromptCarriesTheDayItWasSaid()
    {
        var prompt = SubstrateFactExtractor.BuildPrompt(
            "Marcus had his birthday yesterday", null, new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("2026-09-13", prompt);
    }

    /// <summary>
    /// And the day it carries is the utterance's, not the clock's. This is
    /// what makes the extractor reusable by the boot rebuild: a rebuild
    /// running a year after a turn still resolves "yesterday" against the
    /// day that turn happened.
    /// </summary>
    [Fact]
    public async Task TheDayIsTheUtterancesOwn_NotTheDayOfTheRun()
    {
        var substrate = new StubSubstrate("NONE");
        var extractor = new SubstrateFactExtractor(substrate,
            Options.Create(new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<SubstrateFactExtractor>.Instance);

        await extractor.ExtractAsync(
            new Utterance("u", "Marcus had his birthday yesterday",
                new DateTimeOffset(2019, 4, 2, 0, 0, 0, TimeSpan.Zero), "user"),
            null, CancellationToken.None);

        Assert.Contains("2019-04-02", substrate.Prompt);
    }

    [Fact]
    public void ThePreviousReplyIsContextInThePrompt()
    {
        var prompt = SubstrateFactExtractor.BuildPrompt("I totally agree.", "The first knob is Tier.");
        Assert.Contains("JUST BEFORE", prompt);
        Assert.True(prompt.IndexOf("The first knob is Tier.", StringComparison.Ordinal)
            < prompt.IndexOf("I totally agree.", StringComparison.Ordinal));
    }
}
