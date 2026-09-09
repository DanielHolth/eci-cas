namespace EciCas.Agents.Passages;

/// <summary>
/// The vector half of retrieval. Small numbers on purpose: a passage is the
/// persona's own thought about a stretch of turns, and three of those is
/// context — thirty is the turn's whole prompt spent on second-guessing.
/// </summary>
public sealed class PassageOptions
{
    /// <summary>
    /// How many passages a turn may match. Read by nothing any more:
    /// Hindsight and Librarian both take the budget from
    /// RuntimeKnobs.RecallDepth, so the one slider covers every cosine cut
    /// a turn makes rather than only the ones over archive rows. Kept as a
    /// documented default for the number the corpus was designed around.
    /// </summary>
    public int TopK { get; set; } = 3;

    /// <summary>
    /// Cosine floor. Raised from 0.25 to 0.625 -- the midpoint between the
    /// old floor and a perfect match.
    ///
    /// The low floor was argued from the ring: the hits worth having are the
    /// middling ones, where a thought touches the turn sideways, and a high
    /// floor only returns notes that restate the prompt. That argument is
    /// still the right one about *what a note is for*; what it did not
    /// account for is how much of the bundle a loose hit spends. A note is
    /// prose, it is not cheap in the prompt, and a sideways hit that does not
    /// land is indistinguishable in the bundle from one that does.
    ///
    /// So the trade is deliberate and it is a trade: fewer wakes, and the
    /// ones that come are more nearly about the turn. If the ring goes quiet
    /// -- Reflection writing notes that never wake -- this is the first knob
    /// to walk back, and 0.45 is the obvious next stop rather than 0.25.
    ///
    /// Read by Hindsight and by Librarian's passage lane both, so this moves
    /// prose recall everywhere at once. Reflection's revisit floor is its
    /// own knob (ReflectionOptions.RevisitMinScore) and is untouched.
    /// </summary>
    public double MinScore { get; set; } = 0.625;
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
