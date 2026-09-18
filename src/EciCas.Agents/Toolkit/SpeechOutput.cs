using System.Speech.Synthesis;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// The one place that touches <see cref="SpeechSynthesizer"/>, called by any
/// manifest toolkit whose verb is <c>speak_text</c> -- one Windows-speech
/// dependency behind one method, so a future change to how Morrow speaks
/// (voice selection, rate) has one place to change.
/// </summary>
internal static class SpeechOutput
{
    /// <summary>
    /// <see cref="SpeechSynthesizer.Speak"/> blocks the calling thread until
    /// the sentence finishes, so it runs on a pool thread rather than the
    /// caller's own -- otherwise a long reading would hold the toolkit
    /// queue for as long as it takes to say it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<ToolkitOutcome> SpeakAsync(string text, CancellationToken cancellationToken)
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
