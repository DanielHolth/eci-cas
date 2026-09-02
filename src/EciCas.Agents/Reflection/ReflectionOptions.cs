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

    /// <summary>How many concluded turns accumulate before Reflection scores a batch — mirrors ArchivistOptions.BatchSize / Python's batch_size default.</summary>
    public int BatchSize { get; set; } = 5;

    /// <summary>
    /// How many batches' worth of turns may sit unflushed while the
    /// substrate is unreachable. A failed flush puts its turns back rather
    /// than dropping them, so this is what stops a long outage from growing
    /// the buffer without limit — and from handing the substrate one
    /// enormous prompt the moment it recovers. Oldest turns are shed first.
    /// </summary>
    public int MaxBufferedBatches { get; set; } = 3;

    /// <summary>Minimum persona eagerness (see DriveVectors) for the batch's best-ranked idea to be pushed to events.perception instead of just archived internally.</summary>
    public double EagernessThreshold { get; set; } = 0.6;
}
