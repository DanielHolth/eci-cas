namespace EciCas.Host.TurnLog;

public sealed class TurnLogOptions
{
    /// <summary>How many events stay in memory for a client that connects mid-conversation. Bounded because this is a display buffer, not a store — the archive is the thing that outlives the process.</summary>
    public int Retain { get; set; } = 100;

    /// <summary>
    /// How long an event must go quiet before it is considered finished and
    /// handed to the sinks. Not the Action envelope: Archivist writes and
    /// Reflection's batch land behind the reply, and a record flushed at the
    /// reply would omit exactly the part nobody else reports.
    /// </summary>
    public int SettleMs { get; set; } = 3000;

    /// <summary>Where to append the JSONL log, relative to the content root. Empty means no disk log — off by default, since this is the one part of the surface that writes.</summary>
    public string Path { get; set; } = "";
}
