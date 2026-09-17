namespace EciCas.Core;

public static class FactReliabilityAugmenter
{
    public static async Task<IReadOnlyList<Fact>> ApplyAsync(
        IReadOnlyList<Fact> facts,
        IFactReliabilityScorer scorer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(scorer);

        var annotated = new List<Fact>(facts.Count);
        foreach (var fact in facts)
        {
            var score = await scorer.ScoreAsync(fact, cancellationToken).ConfigureAwait(false);
            annotated.Add(fact with
            {
                Confidence = score.Confidence,
                Freshness = score.Freshness,
                EvaluatedAt = score.EvaluatedAt,
            });
        }

        return annotated;
    }
}
