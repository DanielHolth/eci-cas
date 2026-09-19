namespace EciCas.Agents.Toolkit;

/// <summary>Reads the routed text aloud through Windows speech.</summary>
public sealed class SpeakTextCapability : ICapability
{
    public string Name => "speak_text";

    public string Description => "Reads the person's words aloud with the Windows system voice.";

    public CapabilityRisk Risk => CapabilityRisk.Local;

    public Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(call.Command))
        {
            return Task.FromResult(new ToolkitOutcome(string.Empty, false, "Nothing to read aloud."));
        }

        return OperatingSystem.IsWindows()
            ? SpeechOutput.SpeakAsync(call.Command, cancellationToken)
            : Task.FromResult(new ToolkitOutcome(string.Empty, false, "Text-to-speech isn't available on this machine."));
    }
}
