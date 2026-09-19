using System.Diagnostics;
using System.Text.Json;
using EciCas.Core;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// What the settings capability may change. Implemented by the host, which
/// owns the knobs, their persistence and the overlay. Consent switches and
/// the tier are deliberately not in it: a spoken request never grants
/// consent or spends money.
/// </summary>
public interface IMorrowSettings
{
    /// <summary>One line per setting: name, current value, allowed range.</summary>
    string Describe();

    /// <summary>Applies one change and says what happened, in a line fit to hand to Intent.</summary>
    string Set(string name, string value);

    /// <summary>Makes the applied changes survive a restart. Returns a problem, or null.</summary>
    Task<string?> SaveAsync();
}

/// <summary>
/// "Make your answers longer", "move to the top left": a substrate call turns
/// the ask into named changes against <see cref="IMorrowSettings.Describe"/>,
/// which are applied and saved.
/// </summary>
public sealed class SettingsCapability(ISubstrateProvider substrate, IInstructionStore instructions, IMorrowSettings settings) : ICapability
{
    public const string AgentName = "Toolkit.Settings";

    public string Name => "settings";

    public string Description => "Changes Morrow's own settings (reply length, mood, language, context, overlay position) when the person asks.";

    public CapabilityRisk Risk => CapabilityRisk.Local;

    public async Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        var prompt = InstructionFile.Fill(instructions.For("Settings"), ("settings", settings.Describe()), ("request", call.Command));
        var started = Stopwatch.GetTimestamp();
        SubstrateResult result;
        try
        {
            result = await substrate.CompleteAsync(AgentName, prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't work out which setting to change: {ex.Message}",
                Usage: [new ToolkitSubstrateCall("settings", null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, SubstrateHealth.Classify(ex))]);
        }

        IReadOnlyList<ToolkitSubstrateCall> usage = [new ToolkitSubstrateCall("settings", result, result.Latency.TotalMilliseconds)];
        var changes = Parse(result.Text);
        if (changes is null)
        {
            return new ToolkitOutcome(string.Empty, false, "Couldn't work out which setting to change.", Usage: usage);
        }

        if (changes.Count == 0)
        {
            return new ToolkitOutcome("No setting matches that request; nothing was changed.", true, null, Usage: usage);
        }

        var lines = changes.Select(c => settings.Set(c.Key, c.Value)).ToList();
        if (await settings.SaveAsync().ConfigureAwait(false) is { } problem)
        {
            lines.Add($"Applied for this session, but not saved: {problem}");
        }

        return new ToolkitOutcome(string.Join(Environment.NewLine, lines), true, null, Usage: usage);
    }

    /// <summary>The first JSON object in the reply, as name/value pairs. Null when there is none.</summary>
    private static Dictionary<string, string>? Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
