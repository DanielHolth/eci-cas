namespace EciCas.Host.Telemetry;

public sealed class TelemetryLogOptions
{
    /// <summary>Directory to write telemetry-YYYY-MM-DD.parquet into, relative to the content root. Empty turns the sink off.</summary>
    public string Directory { get; set; } = "telemetry";

    /// <summary>Rows buffered in memory before a flush, whichever of this or <see cref="FlushMs"/> comes first.</summary>
    public int FlushEvery { get; set; } = 25;

    /// <summary>Longest a row waits in memory before it reaches disk.</summary>
    public int FlushMs { get; set; } = 5000;
}
