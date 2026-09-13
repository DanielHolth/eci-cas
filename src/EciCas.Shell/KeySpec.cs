using System.Runtime.InteropServices;
using System.Windows.Input;

namespace EciCas.Shell;

[Flags]
internal enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>
/// One configured key, named the way a person would name it.
///
/// The config says "-" or "|" or "Ctrl+F8", not 0xBD, and a printable
/// character is deliberately preferred over a key name: '|' is Shift and the
/// key right of Backspace on a Norwegian layout, and Shift and the key left of
/// Z on a US one. Naming the character and asking the layout which key that is
/// means the binding follows the keyboard the person is actually typing on --
/// and it is asked again whenever the foreground window's layout changes,
/// because switching layouts mid-session is the ordinary case here, not the
/// exotic one.
///
/// The character carries its own modifiers: the layout says '|' needs Shift,
/// so nothing in the config has to. Explicit "Ctrl+" / "Shift+" / "Alt+" /
/// "Win+" prefixes are ORed on top, which is how a key that is too easy to
/// type by accident gets made harder.
/// </summary>
internal sealed class KeySpec
{
    private readonly char? _character;
    private readonly int _namedVirtualKey;
    private readonly KeyModifiers _extra;

    private IntPtr _layout = IntPtr.Zero;
    private (int VirtualKey, KeyModifiers Modifiers) _resolved;

    private KeySpec(char? character, int namedVirtualKey, KeyModifiers extra)
    {
        _character = character;
        _namedVirtualKey = namedVirtualKey;
        _extra = extra;
        _resolved = (namedVirtualKey, extra);
    }

    /// <summary>What to watch for, on this layout. Cached until the layout
    /// changes, which is most of the time.</summary>
    public (int VirtualKey, KeyModifiers Modifiers) Resolve(IntPtr layout)
    {
        if (_character is null) return (_namedVirtualKey, _extra);
        if (layout == _layout) return _resolved;

        var scan = VkKeyScanExW(_character.Value, layout);

        // -1 means this character is not on this layout at all -- a Cyrillic
        // layout has no '|'. Keeping the last resolution is the kind thing to
        // do: the key stops working while that layout is in front and comes
        // back when it is not, rather than latching onto whatever key 0xFF is.
        if (scan == -1) return _resolved;

        _layout = layout;
        _resolved = ((byte)(scan & 0xFF), Shift((scan >> 8) & 0xFF) | _extra);
        return _resolved;
    }

    /// <summary>The high byte of VkKeyScanEx: 1 Shift, 2 Ctrl, 4 Alt. AltGr is
    /// 6, which is how the OS reports it too, so no special case.</summary>
    private static KeyModifiers Shift(int state)
    {
        var modifiers = KeyModifiers.None;
        if ((state & 1) != 0) modifiers |= KeyModifiers.Shift;
        if ((state & 2) != 0) modifiers |= KeyModifiers.Control;
        if ((state & 4) != 0) modifiers |= KeyModifiers.Alt;
        return modifiers;
    }

    /// <summary>
    /// "Ctrl+Shift+k", "F8", "|". Modifier prefixes first, then either a single
    /// character or one of WPF's <see cref="Key"/> names for the keys that have
    /// no character to name them by.
    /// </summary>
    public static KeySpec Parse(string spec, string what)
    {
        var rest = (spec ?? string.Empty).Trim();
        var extra = KeyModifiers.None;

        // Length > 1 so that "+" on its own, and the "+" at the end of "Ctrl++",
        // are read as the key rather than as a separator with nothing after it.
        while (rest.Length > 1)
        {
            var split = rest.IndexOf('+');
            if (split <= 0) break;

            var modifier = Modifier(rest[..split].Trim());
            if (modifier is null) break;

            extra |= modifier.Value;
            rest = rest[(split + 1)..].Trim();
        }

        if (rest.Length == 0)
        {
            throw new InvalidOperationException($"Shell: the {what} key is empty. Try \"-\", \"|\" or \"Ctrl+F8\".");
        }

        if (rest.Length == 1) return new KeySpec(rest[0], 0, extra);

        if (!Enum.TryParse<Key>(rest, ignoreCase: true, out var key))
        {
            throw new InvalidOperationException(
                $"Shell: '{rest}' is not a key. Use the character itself (\"-\", \"|\") or a name from System.Windows.Input.Key (\"F8\", \"Space\").");
        }

        return new KeySpec(null, KeyInterop.VirtualKeyFromKey(key), extra);
    }

    private static KeyModifiers? Modifier(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" => KeyModifiers.Control,
        "shift" => KeyModifiers.Shift,
        "alt" => KeyModifiers.Alt,
        "win" or "windows" => KeyModifiers.Windows,
        _ => null,
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanExW(char character, IntPtr layout);
}
