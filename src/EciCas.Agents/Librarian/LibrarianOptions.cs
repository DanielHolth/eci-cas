namespace EciCas.Agents.Librarian;

/// <summary>Tier-scaled caps on how Librarian narrows the shelf.</summary>
public sealed class LibrarianOptions
{
    // MaxSelectedPairs and VectorPairs used to live here. They are derived
    // from the Recall-threads knob now (RuntimeKnobs.SelectorLanePairs and
    // VectorLanePairs): every pair either of them named was a file read and a
    // picking call downstream, so the budget belongs to the stage that spends
    // it, stated once.

    /// <summary>
    /// The floor a gloss has to clear to count as a lead. Cosine over a
    /// normalised embedder is generous - unrelated text still scores well
    /// above zero - so without a floor this would always return its full
    /// quota, including on a turn that is about nothing in the archive.
    /// </summary>
    public double VectorMinScore { get; set; } = 0.75;
}
