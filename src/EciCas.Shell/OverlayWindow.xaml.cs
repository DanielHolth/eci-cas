using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace EciCas.Shell;

/// <summary>
/// Morrow resting in a corner: a transparent, always-on-top, taskbar-less
/// window with the overlay page inside it.
///
/// Two states, one key apart. Click-through is the resting state -- she is a
/// watermark, and the mouse goes through her to whatever she is sitting on
/// top of. Interactable is the other one, and then she can be dragged and
/// clicked, and a click opens the real window.
/// </summary>
internal partial class OverlayWindow : Window
{
    private readonly ShellOptions _options;
    private IntPtr _handle;
    private bool _interactable;

    /// <summary>
    /// Where the top edge sits when she is her resting height, which is the
    /// one number Placement stores and the one the person chose by dragging
    /// her there. Growing to fit a long reply moves Top and leaves this alone,
    /// so a tall bubble on screen when she is dragged does not teach the file
    /// a position she never rests at.
    /// </summary>
    private double _restingTop;

    /// <summary>The horizontal counterpart to <see cref="_restingTop"/>: where
    /// the left edge sits when she is her resting width. Growing to fit a wide
    /// bubble moves Left and leaves this alone, keeping her center fixed.
    /// </summary>
    private double _restingLeft;

    /// <summary>The face was clicked while it was interactable.</summary>
    public event Action? SessionRequested;

    public OverlayWindow(ShellOptions options, Uri overlay)
    {
        _options = options;
        InitializeComponent();

        Width = options.Width;
        Height = options.Height;

        Loaded += async (_, _) =>
        {
            await Browser.LoadAsync(View, overlay, chromeless: true);
            View.CoreWebView2.WebMessageReceived += (_, e) => OnPageMessage(e.WebMessageAsJson);

            // After the browser child windows exist, which is the reason this
            // is not just done once at startup -- see ClickThrough.
            View.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                ClickThrough.Set(_handle, _interactable);
                Publish();
            };
        };

        // Not in Loaded: Left/Top set before the window is shown avoids the
        // frame where it appears at the default position and jumps.
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Placement.Restore(this);
            _restingTop = Top;
            _restingLeft = Left;
            ClickThrough.Set(_handle, _interactable);
        };
    }

    /// <summary>Whether clicks land on her or pass through. Toggled by the
    /// interact hotkey and by the tray menu.</summary>
    public bool Interactable
    {
        get => _interactable;
        set
        {
            _interactable = value;
            if (_handle != IntPtr.Zero) ClickThrough.Set(_handle, value);
            Publish();
        }
    }

    /// <summary>The voice hotkey is held. The page draws it; nothing here
    /// depends on it.</summary>
    public bool Listening
    {
        set
        {
            _listening = value;
            Publish();
        }
    }

    private bool _listening;

    /// <summary>
    /// What the shell just heard, or why it heard nothing -- one line, shown
    /// under the face and then forgotten.
    ///
    /// Dictation is the one thing the person cannot check for themselves. They
    /// know what they typed; they do not know what a model made of what they
    /// said, and a transcript that arrived wrong is indistinguishable from a
    /// persona that answered badly unless the words are put on screen. The
    /// counter goes with it so the page can show the same sentence twice
    /// running and still restart its own timer.
    /// </summary>
    public string Heard
    {
        set
        {
            _heard = value;
            _heardAt++;
            Publish();
        }
    }

    private string _heard = string.Empty;
    private int _heardAt;

    /// <summary>The page's half of the contract -- see morrow-eci/lib/shell.ts.
    /// Two messages, and anything else is ignored rather than trusted: the page
    /// is the one part of this that can be updated without rebuilding the
    /// shell.</summary>
    private void OnPageMessage(string json)
    {
        string? type;
        try
        {
            type = JsonDocument.Parse(json).RootElement.TryGetProperty("type", out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return;
        }


        switch (type)
        {
            case "session":
                SessionRequested?.Invoke();
                break;

            case "drag":
                Drag();
                break;

            case "resize":
                Grow(json);
                break;
        }
    }

    /// <summary>
    /// Hands the rest of a gesture already in progress to the OS move loop,
    /// which owns it until the button comes up.
    ///
    /// Not DragMove. That one asks WPF whether the left button is down, and
    /// WPF has no idea: the press landed in a WebView2 child window, a
    /// separate HWND tree it never sees input from, so it answers Released
    /// and DragMove throws before doing anything. Telling the window it was
    /// grabbed by its caption asks nobody's opinion.
    ///
    /// ReleaseCapture first, because the browser child took the mouse on the
    /// press and the move loop will not start while another window holds it.
    /// SendMessage rather than Post, so the nested loop runs inside this call
    /// and the lines after it are reached on release -- which is the moment
    /// there is a new corner worth remembering.
    /// </summary>
    private void Drag()
    {
        if (_handle == IntPtr.Zero) return;

        ReleaseCapture();
        SendMessage(_handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);

        _restingTop = Top + Height - _options.Height;
        _restingLeft = Left + (Width - _options.Width) / 2;
        Placement.Save(this, _restingLeft, _restingTop);
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Takes the size the page says it needs and keeps her bottom edge and her
    /// horizontal center where they are, so the face stays put and a bubble
    /// opens upward and outward into empty desktop rather than off either edge
    /// of a window sized for the face alone.
    ///
    /// Clamped at both ends on both axes. Never smaller than the configured
    /// size, which is what the face alone occupies, and never past MaxHeight /
    /// MaxWidth or the room actually available -- a window that grew off the
    /// screen would put the newest words where nobody can read them.
    /// </summary>
    private void Grow(string json)
    {
        double requestedHeight;
        double requestedWidth;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("height", out var heightProperty)) return;
            if (!heightProperty.TryGetDouble(out requestedHeight)) return;

            // Width is the newer half of this message; a page built against
            // the old contract that only ever sends height still resizes fine.
            requestedWidth = root.TryGetProperty("width", out var widthProperty)
                && widthProperty.TryGetDouble(out var parsedWidth)
                ? parsedWidth
                : _options.Width;
        }
        catch (JsonException)
        {
            return;
        }

        var bottom = _restingTop + _options.Height;
        var heightCeiling = Math.Min(_options.MaxHeight, bottom - SystemParameters.VirtualScreenTop);
        var height = Math.Clamp(requestedHeight, _options.Height, Math.Max(_options.Height, heightCeiling));

        var centerX = _restingLeft + _options.Width / 2;
        var widthCeiling = Math.Min(_options.MaxWidth, SystemParameters.VirtualScreenWidth);
        var width = Math.Clamp(requestedWidth, _options.Width, Math.Max(_options.Width, widthCeiling));

        if (Math.Abs(height - Height) < 1 && Math.Abs(width - Width) < 1) return;

        // Top and Left first. Setting Height/Width alone would push the bottom
        // edge or a side past where she rests before the next line pulls it
        // back, which reads as a twitch on every reply.
        Top = bottom - height;
        Height = height;
        Left = centerX - width / 2;
        Width = width;
    }

    private void Publish()
    {
        if (View.CoreWebView2 is null) return;
        View.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                listening = _listening,
                interactable = _interactable,
                heard = _heard,
                heardAt = _heardAt,
            }));
    }
}
