namespace EciCas.Shell;

/// <summary>
/// The screen's half of the shell's configuration. Everything here is a
/// privacy setting before it is a picture setting.
/// </summary>
internal sealed class ScreenShotOptions
{
    /// <summary>
    /// Off means no capture ever happens -- the same guarantee
    /// <see cref="DictationOptions.Enabled"/> gives the microphone, for the
    /// same reason. Someone who wants to be certain rather than told should
    /// be able to turn the camera off and watch the folder stay empty.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Flat, beside the archive in the build output, so a host
    /// started from the wrong folder has its own -- see AGENTS.md.</summary>
    public string Directory { get; set; } = "screenshots";

    /// <summary>
    /// The long edge in pixels. 2048 because that is where the vendor's own
    /// "high" detail resizes to anyway -- sending more is bytes the API
    /// throws away, and resampling here rather than there is the one place
    /// the filter is ours to choose. A 4K screen lands at 2048x1152, about
    /// 2,800 input tokens.
    ///
    /// Lower is the knob that saves money, and it saves it with the square:
    /// 512 is 173 tokens. It is also the knob that decides whether on-screen
    /// text survives at all, and below about 1024 it does not.
    /// </summary>
    public int MaxEdge { get; set; } = 2048;

    /// <summary>JPEG quality. 70 is the floor before small on-screen text
    /// starts picking up ringing, which is the only content that matters
    /// here.</summary>
    public int Quality { get; set; } = 70;
}
