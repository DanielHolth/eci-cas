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
                    Placement.Save(this);
                }
                catch (InvalidOperationException)
                {
                }
                break;
        }
    }

    private void Publish()
    {
        if (View.CoreWebView2 is null) return;
        View.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(new { listening = _listening, interactable = _interactable }));
    }
}
