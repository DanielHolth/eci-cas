namespace EciCas.Agents.Reflection;

/// <summary>
/// Local loop guard — see plan §3.6. Idea -> arc -> conclusion -> idea would
/// otherwise loop forever while spending on LLM calls, and hop_count can't
/// catch it since each idea is a legitimately new event. Enforced entirely
/// inside Reflection so Governance never grows a fourth job.
/// </summary>
public sealed class ReflectionOptions
{
    public int MaxIdeaGeneration { get; set; } = 1;

    /// <summary>How many concluded turns accumulate before Reflection scores a batch — mirrors ConsolidatorOptions.BatchSize / Python's batch_size default.</summary>
    public int BatchSize { get; set; } = 5;

    /// <summary>Minimum persona eagerness (see DriveVectors) for the batch's best-ranked idea to be pushed to events.perception instead of just archived internally.</summary>
    public double EagernessThreshold { get; set; } = 0.6;
}
