using System.Collections.Generic;
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
/// the deepest window under the cursor first, and WebView2 puts four or five
/// of its own HWNDs inside the WPF window; flagging only the parent leaves the
/// browser child happily swallowing clicks. Hence the walk.
///
/// And hence the bookkeeping. Some of those children were born with the very
/// flags this class hands out, and taking one back off a window that owned it
/// is what made the interact key look like an invisibility key: Chromium's
/// Chrome_RenderWidgetHostHWND is created WS_EX_TRANSPARENT because it is an
/// accessibility shim that must never paint, and a shim told to paint fills
/// the window with opaque black. So the state each window was found in is
/// remembered, and going clickable restores it rather than clearing it.
/// </summary>
internal static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Keeps a click from activating the window as well as from
    /// landing in it: a watermark that steals focus is worse than one that
    /// steals a click, because focus does not come back on its own.</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int Ours = WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

    /// <summary>
    /// Which of <see cref="Ours"/> each window already carried the first time
    /// it was seen. Small -- one entry per HWND of one overlay -- and pruned
    /// as windows die, because handles are reused and a stale answer here
    /// would put a flag back on a window that never had it.
    /// </summary>
    private static readonly Dictionary<IntPtr, int> Born = [];

    public static void Set(IntPtr window, bool clickable)
    {
        Forget();
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

        if (!Born.TryGetValue(window, out var born))
        {
            born = style & Ours;
            Born[window] = born;
        }

        var updated = clickable
            ? (style & ~Ours) | born
            : style | Ours;

        if (updated == style) return;

        // SetWindowLong and nothing else. SWP_FRAMECHANGED was tried here and
        // is wrong twice over: hit testing reads WS_EX_TRANSPARENT live, so it
        // buys nothing, and forcing a frame recalculation on a layered window
        // -- which AllowsTransparency makes this one -- throws away the
        // composited surface.
        SetWindowLong(window, GWL_EXSTYLE, updated);
    }

    private static void Forget()
    {
        if (Born.Count == 0) return;

        List<IntPtr>? gone = null;
        foreach (var window in Born.Keys)
        {
            if (IsWindow(window)) continue;
            gone ??= [];
            gone.Add(window);
        }

        if (gone is null) return;
        foreach (var window in gone) Born.Remove(window);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}
