using System.IO;
using System.Text.Json;
using System.Windows;

namespace EciCas.Shell;

/// <summary>
/// Where the watermark was last put, remembered across runs.
///
/// Beside the exe, like cost.json and level.json and for the same reason: a
/// corner that reset on every launch would make dragging her there pointless.
/// Clamped on the way back in, because the monitor she was on may not be
/// plugged in any more -- and a window restored onto a screen that is gone is
/// a window nobody can find.
/// </summary>
internal sealed record Placement(double Left, double Top)
{
    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "overlay.json");

    /// <summary>
    /// Remembers where she rests. The top edge is passed in rather than read
    /// off the window because the window may be taller than its resting height
    /// at the moment it is dragged -- it grows upward to fit a long reply --
    /// and the corner worth remembering is the one she returns to.
    /// </summary>
    public static void Save(Window window, double top)
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(new Placement(window.Left, top)));
        }
        catch (Exception)
        {
            // A read-only install directory costs the remembered corner and
            // nothing else. Not worth interrupting anyone over.
        }
    }

    /// <summary>Positions the window: where it was, or the bottom-right corner
    /// of the work area on a first run.</summary>
    public static void Restore(Window window)
    {
        var saved = Read();
        var screen = SystemParameters.WorkArea;

        var left = saved?.Left ?? screen.Right - window.Width - 24;
        var top = saved?.Top ?? screen.Bottom - window.Height - 24;

        // Enough of the window has to be on the virtual screen to grab. Not a
        // containment test: with several monitors the virtual screen is not a
        // rectangle, and a window straddling two of them is fine.
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (!virtualScreen.IntersectsWith(new Rect(left, top, window.Width, window.Height)))
        {
            left = screen.Right - window.Width - 24;
            top = screen.Bottom - window.Height - 24;
        }

        window.Left = left;
        window.Top = top;
    }

    private static Placement? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<Placement>(File.ReadAllText(Path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
