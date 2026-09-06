namespace EciCas.Agents.Cataloger;

/// <summary>Count-based batching — deterministic and testable without a timer. Was ArchivistOptions.BatchSize; it moved with the write.</summary>
public sealed class CatalogerOptions
{
    /// <summary>
    /// Turns processed before the pending facts are flushed, not facts
    /// accumulated — most turns state nothing worth remembering, so counting
    /// facts could leave a single just-stated one (a name) sitting invisible
    /// in memory for many turns, waiting for enough *other* facts to arrive.
    /// </summary>
    public int BatchSize { get; set; } = 3;
}
