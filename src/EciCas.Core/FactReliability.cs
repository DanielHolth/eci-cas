namespace EciCas.Core;

public sealed record FactReliabilityScore(
    string FactId,
    double Confidence,
    double Freshness,
    DateTimeOffset EvaluatedAt)
{
    public static FactReliabilityScore Empty(string factId) =>
        new(factId, 0d, 0d, DateTimeOffset.UtcNow);
}

public interface IFactReliabilityScorer
{
    Task<FactReliabilityScore> ScoreAsync(Fact fact, CancellationToken cancellationToken);
}

public sealed class FactReliabilityScorer : IFactReliabilityScorer
{
    public Task<FactReliabilityScore> ScoreAsync(Fact fact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var confidence = ScoreConfidence(fact);
        var freshness = ScoreFreshness(fact);

        return Task.FromResult(new FactReliabilityScore(
            fact.Id,
            Math.Clamp(confidence, 0d, 1d),
            Math.Clamp(freshness, 0d, 1d),
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Scored at mint time, before <c>ThreadWeaver</c> runs — so a fact's own
    /// <see cref="Fact.SupersededBy"/> is never set yet here; that column
    /// lands on the *old* row being retired, not this one. Contradiction as a
    /// confidence signal needs the weaver's verdict, which this scorer does
    /// not see, so it is not attempted rather than faked with a check that
    /// could never fire.
    /// </summary>
    private static double ScoreConfidence(Fact fact)
    {
        var baseScore = 0.72d;

        if (!string.IsNullOrWhiteSpace(fact.OriginModel))
        {
            baseScore += 0.08d;
        }

        if (!string.IsNullOrWhiteSpace(fact.Entity))
        {
            baseScore += 0.05d;
        }

        if (string.Equals(fact.Class, FactClasses.Other, StringComparison.OrdinalIgnoreCase))
        {
            baseScore -= 0.10d;
        }

        return baseScore;
    }

    private static double ScoreFreshness(Fact fact)
    {
        var ageDays = (DateTimeOffset.UtcNow - fact.Timestamp).TotalDays;
        var decay = Math.Clamp(1d - (ageDays / 365d), 0d, 1d);

        return fact.Class switch
        {
            "date" => Math.Clamp(decay + 0.25d, 0d, 1d),
            "relation" => Math.Clamp(decay + 0.10d, 0d, 1d),
            "preference" => Math.Clamp(decay, 0d, 1d),
            "state" => Math.Clamp(decay * 0.8d, 0d, 1d),
            "location" => Math.Clamp(decay * 0.7d, 0d, 1d),
            _ => Math.Clamp(decay * 0.9d, 0d, 1d),
        };
    }
}
