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
    /// Hindsight takes the budget from RuntimeKnobs.RecallDepth, so the one
    /// slider covers every cosine cut a turn makes rather than only the ones
    /// over archive rows. Kept as a documented default for the number the
    /// corpus was designed around.
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
    /// Read by Hindsight's passage lane, so this moves prose recall
    /// everywhere at once. Reflection's revisit floor is its own knob
    /// (ReflectionOptions.RevisitMinScore) and is untouched.
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
    /// embedder: local ONNX finishes long inside the consult sweep, a
    /// remote embedding endpoint might not, and that is a deployment fact
    /// this code cannot know.
    /// </summary>
    public bool WakeWhenAddressed { get; set; } = true;

    /// <summary>
    /// Ceiling on pairs contributed by passages, on top of the ordinary
    /// consult sweep. Bounds the extra work a hit can buy.
    /// </summary>
    public int MaxPairsFromPassages { get; set; } = 3;

    /// <summary>
    /// How many recent wakes a note has to sit out before it may wake again.
    ///
    /// The sweep is stateless and deterministic: score every note, keep the
    /// best few over the floor. So when a conversation stays in one
    /// neighbourhood -- and a conversation about the system itself stays
    /// there for eighty turns -- the same three notes are the best three on
    /// every single turn, and arrive in every single prompt. Measured in a
    /// live session: three identical notes, turn after turn, until Intent
    /// began apologising for them.
    ///
    /// Repetition is not what the floor filters. MinScore rejects a weak
    /// match; it has nothing to say about a strong one that was already in
    /// the last two prompts and added everything it had to add the first
    /// time. The fourth-best note it displaces is, by construction, the one
    /// the persona has not just been told.
    ///
    /// A window rather than a permanent exclusion because a note can be
    /// genuinely central for a stretch of turns, and because a hard forget
    /// would make the corpus behave differently depending on how recently
    /// the host booted. Three turns is the corpus's own design number. Zero
    /// disables it and restores the deterministic sweep exactly.
    /// </summary>
    public int RepeatWindowTurns { get; set; } = 3;
}
