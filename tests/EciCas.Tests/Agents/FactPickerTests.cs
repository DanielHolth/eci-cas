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
    private sealed class StubSubstrate(string reply) : ISubstrateProvider
    {
        public string? Prompt { get; private set; }

        public Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.FromResult(new SubstrateResult(reply, TimeSpan.Zero, null, null));
        }
    }

    private static readonly IReadOnlyList<Consulted> Shortlist =
    [
        Hit("Governance publishes events.bundle."),
        Hit("Susana's birthday is 10.02.2018."),
        Hit("I call Maria Benita Maia at home."),
    ];

    private static Consulted Hit(string text) =>
        new(new Fact(Guid.NewGuid().ToString("n"), "u", text, DateTimeOffset.UnixEpoch, "user", null, []), 0.8, true);

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
            new Utterance("u", "how many kids do i have?", DateTimeOffset.UnixEpoch, "user", null), null, CancellationToken.None);

        Assert.Empty(facts);
    }

    [Fact]
    public async Task EvenAShortStatementIsSentToTheExtractor()
    {
        var substrate = new StubSubstrate("Maia is my daughter.");
        var extractor = new SubstrateFactExtractor(substrate,
            Options.Create(new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<SubstrateFactExtractor>.Instance);

        await extractor.ExtractAsync(new Utterance("u", "maia is my girl", DateTimeOffset.UnixEpoch, "user", null), null, CancellationToken.None);

        Assert.NotNull(substrate.Prompt);
        Assert.DoesNotContain("JUST BEFORE", substrate.Prompt);
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
