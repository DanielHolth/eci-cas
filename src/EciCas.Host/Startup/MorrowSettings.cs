using System.Globalization;
using EciCas.Agents.Toolkit;
using EciCas.Core;
using EciCas.Host.Endpoints;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// The settings a person may change by asking: the knobs the Settings panel
/// shows, minus consent (screen capture) and cost (tier), plus where the
/// overlay sits.
/// </summary>
internal sealed class MorrowSettings(RuntimeKnobs knobs, TierCatalog tiers, IOptions<KnobDefaults> defaults, OverlayAnchor overlay) : IMorrowSettings
{
    public string Describe() => string.Join(Environment.NewLine,
        $"- maxSentences: {knobs.MaxSentences} (1-20) -- the most sentences in one reply",
        $"- mood: {knobs.Mood} (one of {string.Join(", ", Enum.GetNames<Mood>())})",
        $"- language: {knobs.Language} (one of {string.Join(", ", RuntimeKnobs.Languages)}) -- the language listened for when the person talks",
        $"- contextTurns: {knobs.ContextTurns} (0-8) -- how many earlier turns of the conversation she keeps in mind",
        $"- recallDepth: {knobs.RecallDepth} (1-10) -- how much she recalls from memory per turn",
        $"- perceptionChars: {knobs.PerceptionChars} (64-2048) -- how many characters of typed input she reads",
        $"- overlayPosition: where she sits on screen (one of {string.Join(", ", OverlayAnchor.Positions)})");

    public string Set(string name, string value)
    {
        value = value.Trim();
        switch (name.ToLowerInvariant())
        {
            case "maxsentences" when Int(value) is { } n:
                knobs.MaxSentences = n;
                return $"Replies are now up to {knobs.MaxSentences} sentences.";
            case "contextturns" when Int(value) is { } n:
                knobs.ContextTurns = n;
                return $"Keeping {knobs.ContextTurns} earlier turns in mind.";
            case "recalldepth" when Int(value) is { } n:
                knobs.RecallDepth = n;
                return $"Recall depth is now {knobs.RecallDepth}.";
            case "perceptionchars" when Int(value) is { } n:
                knobs.PerceptionChars = n;
                return $"Reading up to {knobs.PerceptionChars} characters of typed input.";
            case "mood" when Enum.TryParse<Mood>(value, ignoreCase: true, out var mood):
                knobs.Mood = mood;
                return $"Mood is now {knobs.Mood}.";
            case "language" when RuntimeKnobs.Languages.Contains(value.ToLowerInvariant()):
                knobs.Language = value.ToLowerInvariant();
                return $"Listening for {knobs.Language} now.";
            case "overlayposition" when OverlayAnchor.Positions.Contains(value.ToLowerInvariant()):
                return overlay.TryMove(value.ToLowerInvariant())
                    ? $"Moved to the {value.ToLowerInvariant()} of the screen."
                    : "The desktop overlay isn't running, so there is nothing to move.";
            default:
                return $"Can't set {name} to \"{value}\".";
        }
    }

    public Task<string?> SaveAsync() => KnobsEndpoints.SaveAllAsync(knobs, tiers, defaults.Value);

    private static int? Int(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
}
