namespace EciCas.Agents.Toolkit;

/// <summary>
/// How long a toolkit may run before <see cref="ToolkitHandlerAgent"/> gives
/// up on it and cancels the process. A script that hangs (waiting on a
/// prompt it will never get, or on a network call that never returns) would
/// otherwise block the handler's queue -- one worker, one script at a time --
/// for the rest of the session.
/// </summary>
public sealed class ToolkitOptions
{
    public int TimeoutSeconds { get; set; } = 30;
}
