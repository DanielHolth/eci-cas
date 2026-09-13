using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EciCas.Shell;

/// <summary>
/// The two global keys, on a message-only window of their own.
///
/// RegisterHotKey, never a WH_KEYBOARD_LL hook. A low-level keyboard hook sees
/// every keystroke on the desktop, which is both more than this needs and the
/// exact signature anti-cheat drivers are built to refuse -- and Steam is
/// where this is going. RegisterHotKey asks the OS for two keys and gets told
/// about those two.
///
/// Push-to-talk needs a release edge and RegisterHotKey only reports presses,
/// so the down stroke starts a short poll of the key's own state. Polling one
/// key at 40ms is cheap and, unlike a hook, tells nobody anything about any
/// other key.
/// </summary>
internal sealed class HotKeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VoiceId = 1;
    private const int InteractId = 2;

    private readonly HwndSource _sink;
    private readonly DispatcherTimer _release;
    private readonly int _voiceVirtualKey;

    /// <summary>The voice key went down, and then came up. Held in between.</summary>
    public event Action? VoiceDown;
    public event Action? VoiceUp;

    /// <summary>The interact key was pressed once.</summary>
    public event Action? Interact;

    public HotKeys(ShellOptions options)
    {
        // HWND_MESSAGE: a window that exists only to receive messages. It has
        // no size, no place on screen and nothing to paint.
        _sink = new HwndSource(new HwndSourceParameters("Morrow.HotKeys") { ParentWindow = new IntPtr(-3) });
        _sink.AddHook(OnMessage);

        var (voiceModifiers, voiceKey) = options.Voice;
        var (interactModifiers, interactKey) = options.Interact;
        _voiceVirtualKey = KeyInterop.VirtualKeyFromKey(voiceKey);

        // MOD_NOREPEAT: one press is one press. Without it a held key fires
        // WM_HOTKEY at the keyboard repeat rate, and the poll below would be
        // restarted thirty times a second by its own key.
        Register(VoiceId, Native(voiceModifiers) | MOD_NOREPEAT, _voiceVirtualKey, options.VoiceKey);
        Register(InteractId, Native(interactModifiers) | MOD_NOREPEAT, KeyInterop.VirtualKeyFromKey(interactKey), options.InteractKey);

        _release = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _release.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(_voiceVirtualKey) & 0x8000) != 0) return;
            _release.Stop();
            VoiceUp?.Invoke();
        };
    }

    private void Register(int id, uint modifiers, int virtualKey, string name)
    {
        // A refusal is almost always another application holding the same
        // combination, and it is worth saying so rather than leaving a key
        // that quietly does nothing for the rest of the session.
        if (!RegisterHotKey(_sink.Handle, id, modifiers, (uint)virtualKey))
        {
            throw new InvalidOperationException(
                $"Shell: could not register the {name} hotkey (error {Marshal.GetLastWin32Error()}). Something else already holds it.");
        }
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case VoiceId:
                handled = true;
                VoiceDown?.Invoke();
                _release.Start();
                break;
            case InteractId:
                handled = true;
                Interact?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>WPF's ModifierKeys and the Win32 MOD_* flags agree on every bit
    /// except Windows, which is 8 in one and 0x08 in the other -- so they agree
    /// on all of them. Mapped by hand anyway, because that is a coincidence and
    /// not a contract.</summary>
    private static uint Native(ModifierKeys modifiers)
    {
        uint native = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) native |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= 0x0008;
        return native;
    }

    public void Dispose()
    {
        _release.Stop();
        UnregisterHotKey(_sink.Handle, VoiceId);
        UnregisterHotKey(_sink.Handle, InteractId);
        _sink.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
