using System.Windows.Input;

namespace EciCas.Shell;

/// <summary>
/// The desktop shell's own knobs, from the "Shell" section of appsettings.json.
///
/// Keys are named with WPF's <see cref="Key"/> and <see cref="ModifierKeys"/>
/// spellings ("OemMinus", "Oem5", "Shift, Control") rather than raw virtual-key
/// numbers: a person editing this file should be able to change the hotkey
/// without looking up 0xBD, and a bad name is a boot-time complaint instead of
/// a hotkey that silently never fires.
/// </summary>
internal sealed class ShellOptions
{
    /// <summary>
    /// Push-to-talk. Held, not pressed: the overlay says "Listening" for as
    /// long as it is down.
    ///
    /// A bare key -- no modifier -- is what was asked for, and it is worth
    /// being explicit about the cost: RegisterHotKey takes the key away from
    /// every other application on the desktop, so while Morrow runs, this key
    /// cannot be typed anywhere. That is the deal a push-to-talk key makes;
    /// it is a config value precisely so someone who types a lot of hyphens
    /// can pay for it with a modifier instead.
    /// </summary>
    public string VoiceKey { get; set; } = "OemMinus";
    public string VoiceModifiers { get; set; } = "None";

    /// <summary>Toggles whether clicks land on Morrow or pass through her to
    /// the desktop. Shift+Oem5 is the pipe key on a US layout.</summary>
    public string InteractKey { get; set; } = "Oem5";
    public string InteractModifiers { get; set; } = "Shift";

    /// <summary>The watermark's size in device-independent pixels. The face
    /// draws to fit, so this is the whole of the overlay's geometry aside from
    /// where it was last dragged.</summary>
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 340;

    /// <summary>Where the window that opens on a click asks for the session,
    /// and where the watermark asks for itself. Relative to the host's own
    /// listening address.</summary>
    public string SessionPath { get; set; } = "/?mute=1";
    public string OverlayPath { get; set; } = "/overlay/";

    public (ModifierKeys Modifiers, Key Key) Voice => Parse(VoiceModifiers, VoiceKey);
    public (ModifierKeys Modifiers, Key Key) Interact => Parse(InteractModifiers, InteractKey);

    private static (ModifierKeys, Key) Parse(string modifiers, string key)
    {
        if (!Enum.TryParse<Key>(key, ignoreCase: true, out var parsedKey))
        {
            throw new InvalidOperationException($"Shell: '{key}' is not a key name. See System.Windows.Input.Key.");
        }

        var parsedModifiers = ModifierKeys.None;
        if (!string.IsNullOrWhiteSpace(modifiers) && !Enum.TryParse(modifiers, ignoreCase: true, out parsedModifiers))
        {
            throw new InvalidOperationException($"Shell: '{modifiers}' is not a modifier list. Try \"Shift\" or \"Shift, Control\".");
        }

        return (parsedModifiers, parsedKey);
    }
}
