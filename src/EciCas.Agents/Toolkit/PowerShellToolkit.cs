using System.Diagnostics;
using EciCas.Core;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Runs a command through <c>powershell.exe</c>. Fully async -- no
/// <c>ReadToEnd</c>/<c>WaitForExit</c> -- so a multi-second script blocks
/// nothing but its own await chain; the handler's queue keeps consuming
/// other topics on the same thread while this one is in flight.
///
/// ToolkitManagerAgent routes here on meaning, not syntax -- "clean up my
/// downloads folder to make disk space" is a valid <paramref
/// name="command"/> to <see cref="ExecuteAsync"/>, not just literal
/// PowerShell. So this toolkit owns its own translation step: a substrate
/// call turns the ask into a script before anything is run. This is the
/// "heavy lifting" that makes the toolkit versatile rather than a thin
/// process wrapper, and it is scoped to this class on purpose -- a future
/// toolkit that does not need it (guide-toolkit, say) should not have to
/// carry the machinery for one it never calls.
/// </summary>
public sealed class PowerShellToolkit(ISubstrateProvider substrate) : IToolkit
{
    public string Name => "powershell";

    public async Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        var script = await TranslateAsync(command, cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{Escape(script)}\"",
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

    /// <summary>
    /// Turns a natural-language ask into an actual script. Falls back to
    /// running <paramref name="command"/> literally if the substrate is
    /// unavailable or fails -- a translation failure should degrade to "try
    /// it as-is," not block the toolkit entirely, since the text may
    /// already be a real script (Intent or the person themselves can paste
    /// one directly).
    /// </summary>
    private async Task<string> TranslateAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var prompt =
                "Translate this request into a single PowerShell script that accomplishes it. " +
                "Respond with only the script, no explanation, no markdown fences. " +
                "If the request already looks like a valid PowerShell command or script, return it unchanged." +
                Environment.NewLine + Environment.NewLine + command;

            var result = await substrate.CompleteAsync("Toolkit.PowerShell", prompt, cancellationToken)
                .ConfigureAwait(false);

            var text = result.Text.Trim();
            return string.IsNullOrWhiteSpace(text) ? command : StripFences(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return command;
        }
    }

    private static string StripFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        var body = firstNewline >= 0 ? text[(firstNewline + 1)..] : text;
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
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
