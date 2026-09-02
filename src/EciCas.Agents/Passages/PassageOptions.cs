namespace EciCas.Agents.Passages;

/// <summary>
/// The vector half of retrieval. Small numbers on purpose: a passage is the
/// persona's own note that it missed something, and three of those is
/// context — thirty is the turn's whole prompt spent on second-guessing.
/// </summary>
public sealed class PassageOptions
{
    /// <summary>How many passages a turn may match.</summary>
    public int TopK { get; set; } = 3;

    /// <summary>
    /// Cosine floor. Below it a hit is noise, and a noisy hit is worse than
    /// none: it both pads Intent's prompt and sends Recall to read a pair
    /// that has nothing to do with the turn.
    /// </summary>
    public double MinScore { get; set; } = 0.45;

    /// <summary>
    /// Ceiling on pairs contributed by passages, on top of whatever Librarian
    /// selected. Bounds the extra Recall workers a hit can buy — the same
    /// reason RecallOptions.MaxConcurrentRecalls exists one stage down.
    /// </summary>
    public int MaxPairsFromPassages { get; set; } = 3;
}
