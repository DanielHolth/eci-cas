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
    /// Nothing is taken from anyone -- the key is watched, not claimed, so
    /// whatever it is still reaches every other app in front. That is why the
    /// default is a key with no character behind it (Pause) rather than a
    /// printable one: a modifier in front of a symbol key (e.g. "Ctrl+-")
    /// looks safe but is not -- Ctrl has no ASCII control code for most
    /// symbols, so plenty of apps (games and chat boxes especially, reading
    /// raw key state rather than WM_CHAR) still see the bare character and
    /// type it anyway. A key with no character never has this problem.
    /// </summary>
    public string VoiceKey { get; set; } = "Pause";

    /// <summary>Toggles whether clicks land on Morrow or pass through her to
    /// the desktop. Shift comes from the character on nearly every layout, so
    /// it is not written here.</summary>
    public string InteractKey { get; set; } = "|";

    /// <summary>The watermark's size in device-independent pixels. The face
    /// draws to fit, so this is the whole of the overlay's geometry aside from
    /// where it was last dragged.</summary>
    public double Width { get; set; } = 260;

    /// <summary>Her resting height -- the face, and nothing above it. The
    /// window grows past this to fit a long reply and comes back down when the
    /// reply goes away.</summary>
    public double Height { get; set; } = 340;

    /// <summary>How far that growing may go. A watermark is not a transcript:
    /// past this the reply is simply longer than the corner can hold, and the
    /// conversation window is a click away.</summary>
    public double MaxHeight { get; set; } = 720;

    /// <summary>How far she may widen to fit a reply or a heard transcript,
    /// growing outward from her horizontal center. The same ceiling in spirit
    /// as <see cref="MaxHeight"/>, on the other axis.</summary>
    public double MaxWidth { get; set; } = 480;

    /// <summary>Where the window that opens on a click asks for the session,
    /// and where the watermark asks for itself. Relative to the host's own
    /// listening address.</summary>
    public string SessionPath { get; set; } = "/?mute=1";
    public string OverlayPath { get; set; } = "/overlay/";

    /// <summary>Speech to text, which is what the voice key is for. Its own
    /// section because it is the only part of the shell with weights.</summary>
    public DictationOptions Dictation { get; set; } = new();

    /// <summary>What the screen looked like when the key armed. Its own
    /// section because it is the only part of the shell that watches
    /// something the person did not address to Morrow.</summary>
    public ScreenShotOptions Screen { get; set; } = new();

    public KeySpec Voice => KeySpec.Parse(VoiceKey, "voice");
    public KeySpec Interact => KeySpec.Parse(InteractKey, "interact");
}
