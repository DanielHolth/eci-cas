namespace EciCas.Host;

/// <summary>Controls how much ConsoleSubscriber prints. Default is the trimmed 6-line view; Verbose restores the full per-envelope trace.</summary>
public sealed class ConsoleOptions
{
    public bool Verbose { get; set; }
}
