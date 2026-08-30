namespace EciCas.Agents.Recall;

/// <summary>
/// Tier policy lives here, not in the store. MaxPerTopic caps how many rows
/// the store returns per selected triple (already sorted by Importance
/// descending) before Recall's own substrate call picks among them.
/// </summary>
public sealed class RecallOptions
{
    public int MaxPerTopic { get; set; } = 50;
}
