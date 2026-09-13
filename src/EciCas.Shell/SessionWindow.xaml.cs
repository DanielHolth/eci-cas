using System.ComponentModel;
using System.Windows;

namespace EciCas.Shell;

/// <summary>
/// The conversation, the debug drawer and the thoughts panel -- the ordinary
/// browser interface, in a window of its own.
///
/// Nothing is replicated here. It is the same client, served by the same
/// process, reading the same replaying feed, which is the entire reason a
/// window opened at turn forty knows about turns one to thirty-nine. The one
/// thing that differs is the query string: ?mute=1, because the overlay is
/// already doing the talking.
///
/// Closing hides rather than disposes. Reopening then costs nothing and the
/// scroll position survives -- and there is no cost to keeping it, because the
/// session it is showing is running in this process either way.
/// </summary>
internal partial class SessionWindow : Window
{
    private readonly Uri _session;
    private bool _loaded;
    private bool _closing;

    public SessionWindow(Uri session)
    {
        _session = session;
        InitializeComponent();
    }

    /// <summary>Shows it, loading the page the first time and only then.</summary>
    public async Task RevealAsync()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (_loaded) return;
        _loaded = true;
        await Browser.LoadAsync(View, _session, chromeless: false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The tray icon is what quits this application. A closed window here
        // means "go back to being a watermark", which is the same gesture the
        // interact key performs, and neither should end the session.
        if (_closing) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>Close it for real, on shutdown.</summary>
    public void Release()
    {
        _closing = true;
        Close();
    }
}
