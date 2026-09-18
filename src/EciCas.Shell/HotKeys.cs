using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace EciCas.Shell;

/// <summary>
/// The two global keys, watched rather than claimed.
///
/// Nothing here registers, hooks or filters anything: one timer asks the OS,
/// forty times a second, whether the two keys happen to be down. That is the
/// whole design, and it is chosen for two reasons.
///
/// The first is that the keys must still work as keys. A push-to-talk key that
/// is also a hyphen is only worth having if the hyphen still arrives in the
/// game, the chat box and the terminal -- and RegisterHotKey, which is what
/// this used to use, takes the key away from every other application on the
/// desktop for as long as Morrow is running. Watching takes nothing away.
///
/// The second is Steam. A WH_KEYBOARD_LL hook would also see both edges
/// without swallowing anything, but a low-level keyboard hook sees every
/// keystroke on the desktop, which is both far more than this needs and the
/// exact signature anti-cheat drivers are built to refuse. GetAsyncKeyState
/// asks about the keys it is given and learns nothing about any other.
///
/// The cost, stated plainly because it is the trade the user chose: a bare key
/// that is not swallowed is a key that fires when it is typed, and it goes on
/// typing wherever focus is. A modifier in front of a symbol does not reliably
/// fix this -- Ctrl+- still reaches plenty of apps as a bare '-', since Ctrl
/// has no control-code mapping for most symbols and games in particular tend
/// to read raw key state rather than the OS's WM_CHAR. The way out that
/// actually holds is binding to a key with no character at all (see
/// ShellOptions.VoiceKey's default, Pause), not a change here.
/// </summary>
internal sealed class HotKeys : IDisposable
{
    private readonly DispatcherTimer _poll;
    private readonly KeySpec _voice;
    private readonly KeySpec _interact;

    private bool _voiceDown;
    private bool _interactDown;

    /// <summary>The voice key went down, and then came up. Held in between.</summary>
    public event Action? VoiceDown;
    public event Action? VoiceUp;

    /// <summary>The interact key was pressed once.</summary>
    public event Action? Interact;

    public HotKeys(ShellOptions options)
    {
        _voice = options.Voice;
        _interact = options.Interact;

        // 40ms: below the ~50ms at which a deliberate tap starts to feel
        // dropped, and far above the cost of four GetAsyncKeyState calls.
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _poll.Tick += (_, _) => Tick();
        _poll.Start();
    }

    private void Tick()
    {
        var layout = ForegroundLayout();

        var voice = Held(_voice, layout);
        if (voice != _voiceDown)
        {
            _voiceDown = voice;
            (voice ? VoiceDown : VoiceUp)?.Invoke();
        }

        var interact = Held(_interact, layout);
        if (interact != _interactDown)
        {
            _interactDown = interact;
            if (interact) Interact?.Invoke();
        }
    }

    /// <summary>
    /// Down, and down with exactly the modifiers the binding asks for. Exact,
    /// not "at least": '-' must not fire while Shift is held, because that is
    /// an underscore and somebody is typing.
    /// </summary>
    private static bool Held(KeySpec spec, IntPtr layout)
    {
        var (virtualKey, modifiers) = spec.Resolve(layout);
        if (!Down(virtualKey)) return false;

        return Down(VK_SHIFT) == modifiers.HasFlag(KeyModifiers.Shift)
            && Down(VK_CONTROL) == modifiers.HasFlag(KeyModifiers.Control)
            && Down(VK_MENU) == modifiers.HasFlag(KeyModifiers.Alt)
            && (Down(VK_LWIN) || Down(VK_RWIN)) == modifiers.HasFlag(KeyModifiers.Windows);
    }

    private static bool Down(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// The keyboard layout of whatever has focus, which is not necessarily
    /// this application's: layouts are per-thread, and the point of asking is
    /// that '|' is a different physical key on a Norwegian layout than on a US
    /// one. Falls back to this thread's layout when the foreground window
    /// belongs to something we may not query.
    /// </summary>
    private static IntPtr ForegroundLayout()
    {
        var window = GetForegroundWindow();
        var thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, IntPtr.Zero);
        return GetKeyboardLayout(thread);
    }

    public void Dispose() => _poll.Stop();

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);
}
