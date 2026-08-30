namespace EciCas.Agents.Reasoning;

/// <summary>Tier-scaled cap on how many index triples Reasoning may select per turn.</summary>
public sealed class ReasoningOptions
{
    public int MaxSelectedTriples { get; set; } = 3;
}
