using EciCas.Core;

namespace EciCas.Tests.Core;

public class FactReliabilityTests
{
    [Fact]
    public async Task SharedCoreReliabilityScoresFacts_WithoutClutteringPayloads()
    {
        var scorer = new FactReliabilityScorer();
        var fact = new Fact(
            Id: "f-1",
            Turn: 7,
            Text: "My daughter's birthday is 14 April.",
            Timestamp: DateTimeOffset.UtcNow.AddDays(-30),
            Speaker: "user",
            Keywords: ["birthday", "daughter"],
            Class: FactClasses.Relation,
            Entity: "daughter",
            Confidence: null,
            Freshness: null);

        var result = await scorer.ScoreAsync(fact, CancellationToken.None);

        Assert.InRange(result.Confidence, 0d, 1d);
        Assert.InRange(result.Freshness, 0d, 1d);
        Assert.Equal("f-1", result.FactId);
        Assert.True(result.Confidence > 0.5d);
    }

    [Fact]
    public async Task ReliabilityScorerIsAppliedWhenFactsAreWritten()
    {
        var fact = new Fact(
            Id: "f-annotated",
            Turn: 11,
            Text: "The project uses a shared core and explicit layers.",
            Timestamp: DateTimeOffset.UtcNow.AddDays(-7),
            Speaker: "user",
            Keywords: ["project", "layers"],
            Class: FactClasses.Relation,
            Entity: "project",
            Confidence: null,
            Freshness: null);

        var annotated = await FactReliabilityAugmenter.ApplyAsync([fact], new FactReliabilityScorer(), CancellationToken.None);

        Assert.NotNull(annotated[0].Confidence);
        Assert.NotNull(annotated[0].Freshness);
        Assert.InRange(annotated[0].Confidence!.Value, 0d, 1d);
        Assert.InRange(annotated[0].Freshness!.Value, 0d, 1d);
    }
}
