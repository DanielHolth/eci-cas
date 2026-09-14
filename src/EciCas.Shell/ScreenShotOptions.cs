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

    /// <summary>The long edge in pixels, which is what a look at the screen
    /// costs: tokens fall with the square of this. 1920 is about 2,400 input
    /// tokens on a patch-priced model.</summary>
    public int MaxEdge { get; set; } = 1920;

    /// <summary>JPEG quality. 70 is the floor before small on-screen text
    /// starts picking up ringing, which is the only content that matters
    /// here.</summary>
    public int Quality { get; set; } = 70;
}
