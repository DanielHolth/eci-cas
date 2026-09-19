using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EciCas.Shell;

/// <summary>
/// The WebView2 settings both windows want, in one place.
///
/// One environment shared between them, not one each: two environments mean
/// two browser processes, two caches and two copies of the same fonts, and
/// they cannot share a user data folder anyway.
/// </summary>
internal static class Browser
{
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>
    /// Whether the disk cache has been emptied yet this run. One flush, on
    /// whichever of the two windows comes up first.
    /// </summary>
    private static bool _flushed;

    /// <summary>
    /// Under LocalAppData rather than beside the exe. WebView2 writes a cache
    /// and a profile here, and the install directory is frequently somewhere
    /// the user cannot write -- the default would fail at exactly the moment
    /// there is no window in which to say so.
    /// </summary>
    public static Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= Create();

    private static Task<CoreWebView2Environment> Create()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morrow", "WebView2");

        // The overlay is the reason for the autoplay override: it owns the
        // voice, and a browser that waits for a click before it will speak
        // would leave the watermark mute until someone poked it -- which is
        // the one gesture a click-through window cannot receive.
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        };

        return CoreWebView2Environment.CreateAsync(userDataFolder: folder, options: options);
    }

    /// <summary>
    /// Brings the control up on the shared environment and navigates it.
    /// <paramref name="chromeless"/> strips the parts of a browser a watermark
    /// has no use for.
    /// </summary>
    public static async Task LoadAsync(WebView2 view, Uri uri, bool chromeless)
    {
        await view.EnsureCoreWebView2Async(await EnvironmentAsync());

        // Everything this browser loads is a file on this disk, served by a
        // host in this same process, so the cache buys nothing and has cost
        // real days: the surface is a static export whose chunks are named by
        // content hash, but index.html is not, and a cached copy of that one
        // document pins the whole window to whatever build wrote it. A fix
        // shipped, built and copied still came up wearing last week's panel.
        //
        // Emptied on the way up rather than left to revalidate, because "the
        // client you are looking at is the client on disk" is worth more here
        // than a few milliseconds of localhost.
        if (!_flushed)
        {
            _flushed = true;
            await view.CoreWebView2.Profile
                .ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache)
                .ConfigureAwait(true);
        }

        var settings = view.CoreWebView2.Settings;

        // A person who can open DevTools on the session window can read every
        // request it makes. The keys live in the host process and never reach
        // the page, so this is the second lock rather than the first -- but a
        // shipped build has no reason to hand out the tools either.
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        settings.AreBrowserAcceleratorKeysEnabled = !chromeless;
        settings.AreDefaultContextMenusEnabled = !chromeless;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsZoomControlEnabled = false;

        // Nothing in this product opens a second window, so a page that asks
        // for one is not given one. The one legitimate ask is a link the person
        // clicked (a References entry, target=_blank): that goes to their
        // default browser, http(s) only, and never into this control.
        view.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" && e.IsUserInitiated)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            }
        };

        view.Source = uri;
    }
}
