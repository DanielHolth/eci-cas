namespace EciCas.Agents.Librarian;

/// <summary>Tier-scaled caps on how Librarian narrows the shelf.</summary>
public sealed class LibrarianOptions
{
    // These were derived from the Recall-threads knob until the inversion
    // retired it. They stay as plain settings because LibrarianAgent is no
    // longer a registered agent -- nothing reads them at runtime, and a live
    // slider for a stage that never runs is a lie on the panel.

    /// <summary>Pairs the vector lane -- passage leads and gloss matches
    /// together -- may contribute.</summary>
    public int VectorLanePairs { get; set; } = 1;

    /// <summary>Pairs the selector call may name.</summary>
    public int SelectorLanePairs { get; set; } = 1;

    /// <summary>
    /// The floor a gloss has to clear to count as a lead. Cosine over a
    /// normalised embedder is generous - unrelated text still scores well
    /// above zero - so without a floor this would always return its full
    /// quota, including on a turn that is about nothing in the archive.
    /// </summary>
    public double VectorMinScore { get; set; } = 0.75;
}
