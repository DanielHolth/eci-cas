namespace EciCas.Core;

/// <summary>
/// Lets the host ask the desktop overlay to move to a named spot on screen.
/// The shell subscribes when it has an overlay; a console host never does,
/// which <see cref="TryMove"/> reports rather than pretending it worked.
/// </summary>
public sealed class OverlayAnchor
{
    public static readonly string[] Positions = ["top-left", "top-right", "bottom-left", "bottom-right", "center"];

    public event Action<string>? MoveRequested;

    public bool TryMove(string position)
    {
        if (MoveRequested is not { } handler)
        {
            return false;
        }

        handler(position);
        return true;
    }
}
