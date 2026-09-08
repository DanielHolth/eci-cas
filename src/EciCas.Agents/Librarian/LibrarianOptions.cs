namespace EciCas.Agents.Librarian;

/// <summary>Tier-scaled caps on how Librarian narrows the shelf.</summary>
public sealed class LibrarianOptions
{
    /// <summary>How many index pairs the selection call may name.</summary>
    public int MaxSelectedPairs { get; set; } = 3;

    /// <summary>
    /// How many pairs the gloss-vector sweep may add beside that selection.
    /// Small on purpose: this is a second opinion, not a second selector, and
    /// every pair it adds is a whole file Recall then reads. Zero turns the
    /// vector layer off without removing it.
    /// </summary>
    public int VectorPairs { get; set; } = 2;

    /// <summary>
    /// The floor a gloss has to clear to count as a lead. Cosine over a
    /// normalised embedder is generous - unrelated text still scores well
    /// above zero - so without a floor this would always return its full
    /// quota, including on a turn that is about nothing in the archive.
    /// </summary>
    public double VectorMinScore { get; set; } = 0.75;
}
