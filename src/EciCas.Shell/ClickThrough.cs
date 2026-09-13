using System.Runtime.InteropServices;

namespace EciCas.Shell;

/// <summary>
/// Makes a window invisible to the mouse, so a watermark sitting on top of
/// everything does not eat the clicks meant for what is underneath it.
///
/// WS_EX_TRANSPARENT rather than colour-keying or a hit-test region: the face
/// is anti-aliased and semi-transparent, so a shape to hit-test does not exist
/// and a keyed colour would leave a fringe around every curve. This is the
/// whole of it -- either Morrow takes the mouse or the desktop does.
///
/// The child windows matter as much as the top-level one. Hit testing finds
/// the deepest window under the cursor first, and WebView2 puts two or three
/// of its own HWNDs inside the WPF window; flagging only the parent leaves the
/// browser child happily swallowing clicks. Hence the walk.
/// </summary>
internal static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Keeps a click from activating the window as well as from
    /// landing in it: a watermark that steals focus is worse than one that
    /// steals a click, because focus does not come back on its own.</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public static void Set(IntPtr window, bool clickable)
    {
        Apply(window, clickable);

        // Best-effort: a WebView2 that has not finished initialising has no
        // child windows yet, which is why callers re-apply after navigation.
        EnumChildWindows(window, (child, _) =>
        {
            Apply(child, clickable);
            return true;
        }, IntPtr.Zero);
    }

    private static void Apply(IntPtr window, bool clickable)
    {
        var style = GetWindowLong(window, GWL_EXSTYLE);
        var updated = clickable
            ? style & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)
            : style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

        if (updated != style)
        {
            SetWindowLong(window, GWL_EXSTYLE, updated);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
