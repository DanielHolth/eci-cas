using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using WinForms = System.Windows.Forms;

namespace EciCas.Shell;

/// <summary>
/// What was on the screen when the voice key armed.
///
/// Taken at the moment the microphone opens rather than when the sentence
/// ends, because the question is about what the person was looking at when
/// they decided to ask -- a dialog they then dismissed, a line of a game's
/// subtitle, the window that was in front before they turned to Morrow. By
/// the time a two-second take has been transcribed the screen has often moved
/// on.
///
/// Every take is captured, used or not. Deciding whether a question is about
/// the screen happens later and elsewhere, and a shot that was not taken
/// cannot be reconsidered: the cost of keeping one is a hundred kilobytes and
/// the cost of missing one is the whole feature. The folder is flat and the
/// names sort chronologically, so it is also the corpus if any of this has to
/// be rebuilt or measured.
///
/// Nothing leaves the machine here. This class writes a file; whether a
/// picture is ever sent to a model is a decision made further in, on a turn
/// that asked for it.
/// </summary>
internal sealed class ScreenShots
{
    /// <summary>0000000-turn-yyyymmddhhmmss.jpg</summary>
    private static readonly Regex Named = new(@"^(\d{7})-turn-", RegexOptions.Compiled);

    private readonly ScreenShotOptions _options;
    private readonly string _directory;
    private int _sequence;

    /// <summary>
    /// The screen as it was when the take now being spoken began, or null if
    /// the last capture failed. Read by whoever comes to ask what the screen
    /// looked like.
    /// </summary>
    public Glance? Latest { get; private set; }

    /// <summary>Why the last capture produced nothing, for a person rather
    /// than a log. Null when the last one worked.</summary>
    public string? Trouble { get; private set; }

    public ScreenShots(ScreenShotOptions options, string baseDirectory)
    {
        _options = options;
        _directory = Path.Combine(baseDirectory, options.Directory);
        _sequence = Resume();
    }

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Grabs the primary screen, writes it, and reads whatever words are on
    /// it -- all off the UI thread, while the person is still talking. A
    /// failed capture must not cost them the sentence they are in the middle
    /// of, so this sets <see cref="Trouble"/> and returns null rather than
    /// throwing.
    /// </summary>
    public Task<Glance?> CaptureAsync() =>
        _options.Enabled ? Task.Run(TakeAsync) : Task.FromResult<Glance?>(null);

    private async Task<Glance?> TakeAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            // The primary screen, not the virtual desktop. Three monitors
            // stitched side by side make an image the long-edge cap then
            // crushes to an unreadable strip -- worse than the one screen the
            // person was actually looking at.
            var bounds = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).Bounds;

            using var shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var canvas = Graphics.FromImage(shot))
            {
                canvas.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            var stem = Path.Combine(_directory, Name());
            var image = stem + ".jpg";

            using var sized = Fit(shot, _options.MaxEdge);
            sized.Save(image, Jpeg, Quality(_options.Quality));

            // Read from the resized image rather than the capture, so the
            // transcript on disk is a transcript of the file beside it. OCR
            // at 2048 loses nothing a screen puts on itself; matching the two
            // matters more, because a disagreement between them is otherwise
            // undebuggable.
            var words = _options.ReadText ? await ScreenText.ReadAsync(sized).ConfigureAwait(false) : string.Empty;
            if (words.Length > 0)
            {
                // Beside the picture, same stem. The pictures are the corpus;
                // the transcripts are what any later pass at learning from
                // on-screen text would train or measure against, and
                // re-running OCR over a year of screenshots to get them back
                // is an afternoon nobody should have to spend.
                await File.WriteAllTextAsync(stem + ".txt", words).ConfigureAwait(false);
            }

            Latest = new Glance(image, await File.ReadAllBytesAsync(image).ConfigureAwait(false), words);
            Trouble = null;
            return Latest;
        }
        catch (Exception failure)
        {
            Latest = null;
            Trouble = $"I could not see the screen: {failure.Message}";
            return null;
        }
    }

    /// <summary>
    /// Shrinks the long edge to <paramref name="maxEdge"/>, and never enlarges.
    ///
    /// This is the only knob on this class that costs money. A vision model
    /// bills by the patch, so tokens fall with the square of this number: the
    /// 1920x1080 default is about 2,400 input tokens, and halving it to 960
    /// is about 600. Text on screen is what stops it going lower -- a UI
    /// label survives 1920 and is a smear at 640.
    /// </summary>
    private static Bitmap Fit(Bitmap source, int maxEdge)
    {
        var edge = Math.Max(source.Width, source.Height);
        if (maxEdge <= 0 || edge <= maxEdge)
        {
            return (Bitmap)source.Clone();
        }

        var scale = (double)maxEdge / edge;
        var target = new Bitmap((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale), PixelFormat.Format32bppArgb);
        using var canvas = Graphics.FromImage(target);
        canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        canvas.DrawImage(source, 0, 0, target.Width, target.Height);
        return target;
    }

    /// <summary>
    /// A counter and a timestamp: the counter so the flat folder sorts in the
    /// order the takes happened even when two land in the same second, the
    /// timestamp so a file found on its own still says when it was.
    /// </summary>
    private string Name()
    {
        var n = Interlocked.Increment(ref _sequence);
        return string.Create(CultureInfo.InvariantCulture, $"{n:D7}-turn-{DateTime.Now:yyyyMMddHHmmss}");
    }

    /// <summary>
    /// Picks up where the last session left off. The counter is derived from
    /// the folder rather than stored beside it, so deleting a shot or moving
    /// the folder cannot leave a sidecar file lying about what is in it.
    /// </summary>
    private int Resume()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*-turn-*.jpg"))
        {
            var match = Named.Match(Path.GetFileName(file));
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n > highest)
            {
                highest = n;
            }
        }

        return highest;
    }

    /// <summary>
    /// JPEG rather than WebP, and the reason is dependencies rather than
    /// taste. Windows ships a WebP decoder and no encoder, so the smaller
    /// format costs an imaging package in the shipped exe. What that buys is
    /// disk: a vision model prices by the patch, so the bytes on the wire
    /// change nothing about what a look at the screen costs. At quality 70
    /// and a 1920 long edge a shot is roughly 120KB, which is a year of
    /// heavy use inside a couple of gigabytes.
    /// </summary>
    private static readonly ImageCodecInfo Jpeg =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static EncoderParameters Quality(int quality) =>
        new(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100)) } };
}

/// <summary>
/// One look at the screen: where it is kept, what it was, and whatever words
/// were on it.
/// </summary>
/// <param name="Path">The JPEG on disk, which stays whether or not anything
/// ever reads it.</param>
/// <param name="Jpeg">The same bytes, already in hand — the vision call wants
/// them immediately and re-reading the file it just wrote is a round trip for
/// nothing.</param>
/// <param name="Words">What the local OCR read, newline-separated. Empty is a
/// real answer and not a failure: a fullscreen game with no HUD text has none.</param>
internal sealed record Glance(string Path, byte[] Jpeg, string Words);
