namespace EciCas.Agents.Reasoning;

/// <summary>Tier-scaled cap on how many index pairs Reasoning may select per turn.</summary>
public sealed class ReasoningOptions
{
    public int MaxSelectedPairs { get; set; } = 3;
}
