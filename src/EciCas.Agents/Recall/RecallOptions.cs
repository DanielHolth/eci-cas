namespace EciCas.Agents.Recall;

/// <summary>
/// Tier policy lives here, not in the store — see plan §3.3. Mirrors the
/// Python prototype's SWARM_TIERS (agents/governance/knowledge_swarm.py):
/// MaxPaths is that table's "agents" knob (how many proposed lookup paths
/// get queried at all), MaxPerPath is "max_results_per_agent" (how many
/// records come back per path). Values are scaled down from Python's,
/// which post-filters with relevance scoring/diversification before
/// anything reaches Intent — Recall has no such filter, so its caps are
/// the actual ceiling on what Intent sees, not a pre-filter budget.
/// </summary>
public sealed class RecallOptions
{
    public int MaxPerPath { get; set; } = 3;

    /// <summary>Caps how many of Reasoning's proposed paths are actually queried — later paths are dropped, not deprioritized.</summary>
    public int MaxPaths { get; set; } = int.MaxValue;
}
