namespace EciCas.Agents.Archivist;

/// <summary>Count-based batching — deterministic and testable without a timer.</summary>
public sealed class ArchivistOptions
{
    public int BatchSize { get; set; } = 3;
}
