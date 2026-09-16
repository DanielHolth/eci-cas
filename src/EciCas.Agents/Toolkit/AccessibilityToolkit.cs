using System.Speech.Synthesis;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Speaks text aloud through the Windows speech engine. This is the missing
/// output leg next to Sight's own reading flow: <c>SightAgent.ReadAsync</c>
/// already turns a screen into prose and publishes it as a normal reply, so
/// asking "read the screen to me" is answered in text like any other turn.
/// What nothing did until now is say it out loud -- for a person who cannot
/// or would rather not read the reply, or wants any given piece of text (an
/// error dialog, a paragraph pasted in) spoken back to them. This toolkit
/// takes the words -- whatever ToolkitManagerAgent routed here, including a
/// reply Sight already composed -- and speaks them; it does not capture the
/// screen itself, so there is exactly one place that decides what "the
/// screen" says.
/// </summary>
public sealed class AccessibilityToolkit : IToolkit
{
    public string Name => "accessibility";

    public Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Task.FromResult(new ToolkitOutcome(string.Empty, false, "Nothing to read aloud."));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new ToolkitOutcome(string.Empty, false, "Text-to-speech isn't available on this machine."));
        }

        return SpeakAsync(command, cancellationToken);
    }

    /// <summary>
    /// <see cref="SpeechSynthesizer.Speak"/> blocks the calling thread until
    /// the sentence finishes, so it runs on a pool thread rather than the
    /// handler's own -- otherwise a long reading would hold the toolkit
    /// queue for as long as it takes to say it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<ToolkitOutcome> SpeakAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() =>
            {
                using var synth = new SpeechSynthesizer();
                synth.SetOutputToDefaultAudioDevice();
                synth.Speak(text);
            }, cancellationToken).ConfigureAwait(false);

            return new ToolkitOutcome("Read that aloud.", true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't speak that: {failure.Message}");
        }
    }
}
