namespace EciCas.Agents.Archivist;

/// <summary>
/// The bus vocabulary for "the archive grew" — a SystemControl envelope
/// carrying one of these kinds, published by whichever agent is holding the
/// pen (Scribe today, Reflection for its own ideas). Extraction moved from
/// ArchivistAgent's address-and-keyword extractor to Scribe's full-sentence
/// one (see ScribeAgent's remarks), but Identity, Impulse, Governance and the
/// turn log all key off these constants rather than the agent name, so the
/// vocabulary outlived the agent that first defined it.
/// </summary>
public static class ArchiveWriteSignal
{
    public const string ControlKindKey = "control.kind";
    public const string WrittenKind = "Written";

    /// <summary>What the flush actually put on disk, one "path = value" string per record — the same strings the log line prints, so the surface and the console agree without either reading the other.</summary>
    public const string WrittenRecordsKey = "archivist.written";

    /// <summary>The fact ids behind those strings, in the same order. What lets a surface offer to correct a row rather than only to read it — a made-up fact is worth nothing if the only way to remove it is a parquet tool.</summary>
    public const string WrittenIdsKey = "archivist.written.ids";
}
