namespace EciCas.Shell;

/// <summary>
/// The desktop shell's own knobs, from the "Shell" section of appsettings.json.
///
/// Keys are named by the character they type -- "-", "|" -- with optional
/// "Ctrl+" / "Shift+" / "Alt+" / "Win+" prefixes, and are resolved through
/// whatever keyboard layout is in front at the time. See <see cref="KeySpec"/>
/// for why that indirection is worth having.
/// </summary>
internal sealed class ShellOptions
{
    /// <summary>
    /// Push-to-talk. Held, not pressed: the overlay says "Listening" for as
    /// long as it is down.
    ///
    /// Nothing is taken from anyone -- the key is watched, not claimed, so it
    /// still types a hyphen in a game, a chat box or a terminal. The other side
    /// of that same coin: typing a hyphen opens the microphone. Someone who
    /// writes a lot of hyphens should put a modifier in front of it here.
    /// </summary>
    public string VoiceKey { get; set; } = "-";

    /// <summary>Toggles whether clicks land on Morrow or pass through her to
    /// the desktop. Shift comes from the character on nearly every layout, so
    /// it is not written here.</summary>
    public string InteractKey { get; set; } = "|";

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

    public KeySpec Voice => KeySpec.Parse(VoiceKey, "voice");
    public KeySpec Interact => KeySpec.Parse(InteractKey, "interact");
}
