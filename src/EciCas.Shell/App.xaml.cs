using System.Windows;
using EciCas.Host.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinForms = System.Windows.Forms;

namespace EciCas.Shell;

/// <summary>
/// The desktop front end: the same boot the console host runs, followed by a
/// window instead of a prompt.
///
/// One process, deliberately. The substrate, the bus, every agent, the HTTP
/// surface and the exported client all live here, which is what makes this a
/// single exe to ship and -- the part that matters for the constraint -- the
/// only place an API key ever exists. Nothing the page can reach has one: the
/// client calls /api/..., the host calls the vendor, and the two halves of
/// that are separated by a process boundary that happens to be inside one
/// executable.
/// </summary>
public partial class App : System.Windows.Application
{
    private WebApplication? _host;
    private WinForms.NotifyIcon? _tray;
    private OverlayWindow? _overlay;
    private SessionWindow? _session;
    private HotKeys? _hotKeys;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Up before the substrate is, so a long warm-up on a cold local model
        // is a tray icon saying so rather than several silent minutes.
        _tray = new WinForms.NotifyIcon
        {
            // The stock application icon, for now. A drawn one is an asset
            // decision, and an undrawn one should not hold up the window.
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Morrow — waking up",
            Visible = true,
        };

        try
        {
            // ClientPath ahead of the caller's arguments so a real
            // --Surface:ClientPath= on the command line still wins: the
            // command-line provider keeps the last value for a key. This is
            // where the exported client lands -- see the csproj.
            _host = await HostBoot.StartAsync([.. new[] { "--Surface:ClientPath=client" }, .. e.Args]);
        }
        catch (Exception failure)
        {
            // Nothing to fall back to: without the host there is no session,
            // no client and nothing for a window to show.
            Fail("Morrow could not start.", failure);
            return;
        }

        var configuration = _host.Services.GetRequiredService<IConfiguration>();
        var options = configuration.GetSection("Shell").Get<ShellOptions>() ?? new ShellOptions();
        var root = Root(configuration);

        try
        {
            _hotKeys = new HotKeys(options);
        }
        catch (Exception failure)
        {
            // Survivable, unlike the host: the tray menu does everything the
            // keys do, so this is a warning and the session continues.
            WinForms.MessageBox.Show(
                $"{failure.Message}\n\nThe tray menu still works.",
                "Morrow", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }

        _session = new SessionWindow(new Uri(root, options.SessionPath));

        _overlay = new OverlayWindow(options, new Uri(root, options.OverlayPath));
        _overlay.SessionRequested += () => _ = _session.RevealAsync();
        _overlay.Show();

        if (_hotKeys is not null)
        {
            _hotKeys.Interact += () => _overlay.Interactable = !_overlay.Interactable;

            // Held means listening; released means it is not. What is missing
            // between those two is dictation itself: there is no speech-to-text
            // anywhere in this repository yet, so for now the key tells the
            // face it is being listened to and nothing transcribes. The seam is
            // exactly here -- on release, POST the transcript to /api/perceive,
            // which is the same call the text box makes.
            _hotKeys.VoiceDown += () => _overlay.Listening = true;
            _hotKeys.VoiceUp += () => _overlay.Listening = false;
        }

        _tray.Text = "Morrow";
        _tray.DoubleClick += (_, _) => _ = _session.RevealAsync();
        _tray.ContextMenuStrip = TrayMenu();
    }

    /// <summary>
    /// Where the client and the API both are. Read from the host's own
    /// configuration rather than assumed, so moving the surface to another port
    /// moves the windows with it; a wildcard binding is rewritten to localhost,
    /// which is the only address a window on this machine can use anyway.
    /// </summary>
    private static Uri Root(IConfiguration configuration)
    {
        var url = configuration["Surface:Url"] ?? "http://localhost:5179";
        return new Uri(url.Replace("://*", "://localhost").Replace("://+", "://localhost"));
    }

    private WinForms.ContextMenuStrip TrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        menu.Items.Add("Open conversation", null, (_, _) => _ = _session!.RevealAsync());

        var interact = new WinForms.ToolStripMenuItem("Clickable") { CheckOnClick = true };
        interact.CheckedChanged += (_, _) => _overlay!.Interactable = interact.Checked;
        menu.Items.Add(interact);

        // The menu is opened from the tray, which the interact hotkey can have
        // changed since it was last looked at.
        menu.Opening += (_, _) => interact.Checked = _overlay!.Interactable;

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        return menu;
    }

    private void Fail(string message, Exception failure)
    {
        _tray!.Visible = false;
        WinForms.MessageBox.Show($"{message}\n\n{failure.Message}", "Morrow",
            WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotKeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _session?.Release();
        _overlay?.Close();

        // Blocking, and worth the wait: the archive writer batches, and the
        // turn log settles on a timer (TurnLog:SettleMs). Quitting out from
        // under either of them loses the last turn of the conversation -- and
        // that archive is the save file this eventually hands to Steam Cloud.
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
