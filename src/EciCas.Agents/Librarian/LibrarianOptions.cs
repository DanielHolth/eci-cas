namespace EciCas.Agents.Librarian;

/// <summary>Tier-scaled cap on how many index pairs Librarian may select per turn.</summary>
public sealed class LibrarianOptions
{
    public int MaxSelectedPairs { get; set; } = 3;
}
