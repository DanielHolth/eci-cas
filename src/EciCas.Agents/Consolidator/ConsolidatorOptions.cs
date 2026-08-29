namespace EciCas.Agents.Consolidator;

/// <summary>Count-based batching — deterministic and testable without a timer.</summary>
public sealed class ConsolidatorOptions
{
    public int BatchSize { get; set; } = 3;
}
