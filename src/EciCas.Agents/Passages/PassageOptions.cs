namespace EciCas.Agents.Passages;

/// <summary>
/// The vector half of retrieval. Small numbers on purpose: a passage is the
/// persona's own thought about a stretch of turns, and three of those is
/// context — thirty is the turn's whole prompt spent on second-guessing.
/// </summary>
public sealed class PassageOptions
{
    /// <summary>How many passages a turn may match.</summary>
    public int TopK { get; set; } = 3;

    /// <summary>
    /// Cosine floor, deliberately low. A high floor only returns notes that
    /// restate the prompt — the persona rediscovering what it already knew it
    /// was looking for, which is the one thing a note cannot usefully add. The
    /// hits worth having are the middling ones, where a thought touches the
    /// turn sideways. TopK is the budget, not this: the floor rejects noise,
    /// the cap decides how much gets through. Was 0.45 when a passage was a
    /// miss note whose pairs bought Recall workers; MaxPairsFromPassages
    /// bounds that cost on its own.
    /// </summary>
    public double MinScore { get; set; } = 0.25;

    /// <summary>
    /// Wake notes on a turn addressed to the persona ("do you have any
    /// thoughts about that?"), not only on Reflection's own reposted ideas.
    ///
    /// Hindsight was narrowed to self-triggered turns because an embed and a
    /// search sat on the fast path of every reply. The gate keeps that
    /// property: it is a regex, so evaluating it is free, and second-person
    /// turns are a small enough minority that the amortised cost stays near
    /// zero. Off turns it into exactly the agent it was before.
    ///
    /// A knob rather than a hardcoded branch because the cost depends on the
    /// embedder: local ONNX finishes long inside Librarian plus Recall, a
    /// remote embedding endpoint might not, and that is a deployment fact
    /// this code cannot know.
    /// </summary>
    public bool WakeWhenAddressed { get; set; } = true;

    /// <summary>
    /// Ceiling on pairs contributed by passages, on top of whatever Librarian
    /// selected. Bounds the extra Recall workers a hit can buy — the same
    /// reason RecallOptions.MaxConcurrentRecalls exists one stage down.
    /// </summary>
    public int MaxPairsFromPassages { get; set; } = 3;
}
