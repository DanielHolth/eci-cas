namespace EciCas.Agents.Recall;

/// <summary>Tier policy lives here, not in the store — see plan §3.3.</summary>
public sealed class RecallOptions
{
    public int MaxPerPath { get; set; } = 3;
}
