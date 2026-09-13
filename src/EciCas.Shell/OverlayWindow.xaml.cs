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
                // Mid-gesture: the page sends this once the pointer has moved
                // with the button still down, and DragMove hands the rest of
                // the gesture to the OS move loop, which ends on release.
                // Throws if the button came up in between, which is a race and
                // not a fault.
                try
                {
                    DragMove();
                    _restingTop = Top + Height - _options.Height;
                    Placement.Save(this, _restingTop);
                }
                catch (InvalidOperationException)
                {
                }
                break;

            case "resize":
                Grow(json);
                break;
        }
    }

    /// <summary>
    /// Takes the height the page says it needs and keeps her bottom edge
    /// where it is, so the face stays put and the bubble opens upward into
    /// empty desktop.
    ///
    /// Clamped at both ends. Never shorter than the configured height, which
    /// is what the face alone occupies, and never taller than MaxHeight or
    /// than the room actually above her -- a window that grew off the top of
    /// the screen would put the newest words where nobody can read them.
    /// </summary>
    private void Grow(string json)
    {
        double requested;
        try
        {
            if (!JsonDocument.Parse(json).RootElement.TryGetProperty("height", out var property)) return;
            if (!property.TryGetDouble(out requested)) return;
        }
        catch (JsonException)
        {
            return;
        }

        var bottom = _restingTop + _options.Height;
        var ceiling = Math.Min(_options.MaxHeight, bottom - SystemParameters.VirtualScreenTop);
        var height = Math.Clamp(requested, _options.Height, Math.Max(_options.Height, ceiling));

        if (Math.Abs(height - Height) < 1) return;

        // Top first. Setting Height alone would push the bottom edge down over
        // whatever she is resting above before the next line pulls it back,
        // which reads as a twitch on every reply.
        Top = bottom - height;
        Height = height;
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
