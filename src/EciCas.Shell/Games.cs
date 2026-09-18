using System.Runtime.InteropServices;

namespace EciCas.Shell;

/// <summary>
/// Keeping Morrow above a game without asking the person to tune the game.
///
/// Three cases, handled from the outside -- window styles and z-order only,
/// nothing injected into the game's process, so there is nothing here for
/// anti-cheat to object to:
///
/// Borderless (what most modern "Fullscreen" modes really are) needs only
/// that she be put back on top when the game pushes itself above her.
/// Windowed at the monitor's own resolution gets its frame stripped and is
/// stretched over the monitor, which turns it into the borderless case.
/// Exclusive fullscreen owns the display and nothing can draw over it; that
/// is reported so the caller can say so, and voice carries on regardless.
///
/// Event-driven, no timer: <see cref="Watch"/> fires on every foreground
/// change, and the caller also calls <see cref="Surface"/> on its hotkeys.
/// </summary>
internal sealed class Games : IDisposable
{
    private readonly IntPtr _overlay;
    private readonly bool _deborder;
    private readonly WinEventProc _callback;
    private readonly IntPtr _hook;

    /// <summary>The foreground window is running exclusive fullscreen, where
    /// no overlay can show. Raised once per foreground change into that state.</summary>
    public event Action? Exclusive;

    public Games(IntPtr overlay, bool deborder)
    {
        _overlay = overlay;
        _deborder = deborder;

        // Held in a field: the hook calls back through this delegate for as
        // long as it lives, and a collected one crashes the process.
        _callback = (_, _, window, _, _, _, _) => OnForeground(window);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Back to the top of the topmost band, without taking focus --
    /// focus leaving a game is what minimizes it.</summary>
    public void Surface()
    {
        SetWindowPos(_overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void OnForeground(IntPtr window)
    {
        if (window == IntPtr.Zero || window == _overlay || Ours(window)) return;

        if (ExclusiveFullscreen())
        {
            Exclusive?.Invoke();
            return;
        }

        if (_deborder) Deborder(window);
        Surface();
    }

    /// <summary>
    /// Strips the frame off a window whose client area is exactly its
    /// monitor -- a game set to "Windowed" at native resolution, the frame
    /// hanging off the screen edges. The test is what keeps this off
    /// ordinary apps: a maximized browser or editor has a caption and a
    /// taskbar eating into its client area, so it never matches.
    /// </summary>
    private static void Deborder(IntPtr window)
    {
        var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0 || (style & WS_CHILD) != 0) return;

        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        if (!GetClientRect(window, out var client)) return;
        var screen = info.Monitor;
        if (Math.Abs(client.Right - (screen.Right - screen.Left)) > 2 ||
            Math.Abs(client.Bottom - (screen.Bottom - screen.Top)) > 2) return;

        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style));

        var ex = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
        SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(ex));

        SetWindowPos(window, IntPtr.Zero, screen.Left, screen.Top, screen.Right - screen.Left, screen.Bottom - screen.Top,
            SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static bool ExclusiveFullscreen() =>
        SHQueryUserNotificationState(out var state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;

    private static bool Ours(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    private delegate void WinEventProc(IntPtr hook, uint @event, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
        WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_CHILD = 0x40000000;
    private const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000, WS_EX_WINDOWEDGE = 0x100;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
