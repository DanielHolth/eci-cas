namespace EciCas.Agents.Toolkit;

/// <summary>
/// The fixed half of what <see cref="GuideToolkit"/> says: how Morrow
/// herself works, independent of which toolkits happen to be registered.
/// Kept as data rather than folded into <see cref="GuideToolkit"/>'s method
/// body so it reads as documentation and stays easy to keep current as the
/// shell grows -- a wrong keybinding here is a support question, not a typo
/// in a comment nobody sees.
///
/// The two bindings a new person needs before anything else -- hold "-" to
/// talk, "|" to toggle whether Morrow is clickable -- lead every section,
/// per "tell me about yourself" being the first thing a person is expected
/// to ask.
/// </summary>
internal static class MorrowGuide
{
    public const string AboutMorrow =
        """
        Two things to know before anything else: hold the "-" key to talk to me -- press and hold, I listen while it's down, and let go when you're done. And "|" toggles whether I'm clickable: by default clicks pass through me to whatever's underneath, so I don't get in your way; "|" (or the tray menu's "Clickable" box) switches that so you can click and drag me instead.

        Talking to me: hold "-" and speak, or just type in the conversation window. I take a look at your screen automatically whenever you start talking or typing, so I generally already know what you're looking at -- you don't have to describe it.

        Finding me: I sit as a small watermark on your desktop. Double-click my tray icon (or right-click it and choose "Open conversation") to open the full conversation window, which has more room than my little corner does.

        Settings live in the left-hand panel of the conversation window:
        - Profile & display: my name, a light/dark theme, which voice I speak with, how long a reply stays on screen before it fades, and which language I listen for when you talk (English, French, Spanish, or German -- pin one so I stop guessing mid-sentence).
        - Knobs: your tier (Mock/Free/Budget/Pro/Premium), how long my replies are, how much of what you say I take in, how much conversation history I keep in context, my mood, how often I reflect on things, and how far back I search when recalling something. There's also a checkbox there to turn the PowerShell toolkit on, since running commands on your machine is opt-in.

        Toolkits are the things I can actually *do*, beyond talking -- ask "what can you do" any time and I'll list whichever ones are turned on for your tier.
        """;
}
