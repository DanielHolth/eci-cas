using EciCas.RetrievalProbe;

namespace EciCas.Tests.Tools;

/// <summary>
/// The probe itself can only be run where the weights and a real archive
/// are, which is not here. Its arithmetic can be checked anywhere, and it is
/// the part a wrong answer would be believed from: a hit@3 that counted a
/// miss as a hit would green-light the retrieval rewrite on a number that
/// meant nothing.
/// </summary>
public class ScoringTests
{
    [Fact]
    public void RankOf_FindsTheExpectedAddress_CaseInsensitively()
    {
        string[] ranked = ["person/family/son/marcus/name", "assistant/identity/persona/this/name"];

        Assert.Equal(0, Scoring.RankOf(ranked, "PERSON/family/son/marcus/name"));
        Assert.Equal(1, Scoring.RankOf(ranked, "assistant/identity/persona/this/name"));
    }

    [Fact]
    public void RankOf_WhenTheRowWasNeverScored_IsAbsentRatherThanLast()
    {
        Assert.Equal(-1, Scoring.RankOf(["person/family/son/marcus/name"], "person/family/daughter/maia/name"));
    }

    /// <summary>
    /// A rank of -1 must contribute nothing to MRR. Folding it in as a large
    /// rank would still add a positive term and quietly inflate every run
    /// that failed to retrieve at all.
    /// </summary>
    [Fact]
    public void Summarize_TreatsAnAbsentRowAsZero_NotAsAWeakHit()
    {
        var summary = Scoring.Summarize([-1, -1]);

        Assert.Equal(0, summary.Hit1);
        Assert.Equal(0, summary.Hit3);
        Assert.Equal(0, summary.Mrr);
    }

    [Fact]
    public void Summarize_CountsFirstPlaceForHit1_AndTopThreeForHit3()
    {
        // ranks 0, 2, 3: first place once, top three twice, fourth place once.
        var summary = Scoring.Summarize([0, 2, 3]);

        Assert.Equal(3, summary.Questions);
        Assert.Equal(1.0 / 3, summary.Hit1, 6);
        Assert.Equal(2.0 / 3, summary.Hit3, 6);
        Assert.Equal((1.0 + 1.0 / 3 + 1.0 / 4) / 3, summary.Mrr, 6);
    }

    [Fact]
    public void Summarize_WithNoQuestions_IsEmptyRatherThanDivideByZero()
    {
        var summary = Scoring.Summarize([]);

        Assert.Equal(0, summary.Questions);
        Assert.Equal(0, summary.Mrr);
    }
}
