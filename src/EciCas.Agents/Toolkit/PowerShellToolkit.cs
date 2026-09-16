using System.Diagnostics;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Runs a command through <c>powershell.exe</c>. Fully async -- no
/// <c>ReadToEnd</c>/<c>WaitForExit</c> -- so a multi-second script blocks
/// nothing but its own await chain; the handler's queue keeps consuming
/// other topics on the same thread while this one is in flight.
/// </summary>
public sealed class PowerShellToolkit : IToolkit
{
    public string Name => "powershell";

    public async Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{Escape(command)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start);
        if (process is null)
        {
            return new ToolkitOutcome(string.Empty, false, "Failed to start PowerShell.");
        }

        // Both streams read concurrently with the exit wait, not after it --
        // a script that writes enough to fill a pipe buffer before exiting
        // deadlocks a WaitForExit-then-Read ordering.
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ToolkitOutcome(string.Empty, false, "Timed out.");
        }

        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();

        if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
        {
            return new ToolkitOutcome(output, false, error);
        }

        return new ToolkitOutcome(output, process.ExitCode == 0, string.IsNullOrWhiteSpace(error) ? null : error);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }

    private static string Escape(string command) => command.Replace("\"", "`\"");
}
