namespace EciCas.RetrievalProbe;

/// <summary>
/// The measurement, kept apart from the IO so it can be tested without an
/// ONNX session or an archive on disk. Everything here is pure.
/// </summary>
public static class Scoring
{
    /// <summary>Rank of the expected address in a scored list, or -1 if absent.</summary>
    public static int RankOf(IReadOnlyList<string> rankedAddresses, string expected)
    {
        for (var i = 0; i < rankedAddresses.Count; i++)
        {
            if (string.Equals(rankedAddresses[i], expected, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// hit@1 and hit@3 are what the redesign actually rides on: top-K goes to
    /// Intent unfiltered, so a fact ranked fourth is a fact the persona does
    /// not have. MRR is reported beside them because it separates "just
    /// missed" from "nowhere near" — two runs can share a hit@3 and mean very
    /// different things about whether the representation is nearly working.
    /// </summary>
    public static Summary Summarize(IReadOnlyList<int> ranks)
    {
        if (ranks.Count == 0)
        {
            return new Summary(0, 0, 0, 0);
        }

        return new Summary(
            Questions: ranks.Count,
            Hit1: ranks.Count(r => r == 0) / (double)ranks.Count,
            Hit3: ranks.Count(r => r >= 0 && r < 3) / (double)ranks.Count,
            Mrr: ranks.Sum(r => r < 0 ? 0.0 : 1.0 / (r + 1)) / ranks.Count);
    }
}

public sealed record Summary(int Questions, double Hit1, double Hit3, double Mrr);
