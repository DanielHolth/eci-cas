using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace EciCas.Shell;

/// <summary>
/// The words on the screen, read locally.
///
/// Windows.Media.Ocr: in the box since Windows 10 2004, no weights to ship,
/// no GPU, no network, tens of milliseconds. It is not clever and is not
/// meant to be -- it cannot tell you whether something is hiding behind the
/// door, and asked what a screen is about it has no opinion. What it does is
/// the one thing a 512-pixel look at a screen cannot do at all: read a
/// tooltip, an item name, a line of quest text, a paragraph someone wants
/// read aloud to them.
///
/// That division is the whole reason both run. The cheap look supplies the
/// scene and the OCR supplies the letters, they run at the same time, and
/// between them the expensive look is rarely needed.
/// </summary>
internal static class ScreenText
{
    /// <summary>
    /// Null when this machine has no OCR language installed. A language pack
    /// is a Windows setting, not something to install on a person's behalf,
    /// so the absence is reported and the rest of the turn carries on.
    /// </summary>
    private static readonly OcrEngine? Engine = Create();

    public static bool Available => Engine is not null;

    private static OcrEngine? Create()
    {
        try
        {
            // Whatever the machine is set up to read. A companion that is
            // spoken to in two languages should not be pinned to one here
            // either -- OCR profiles follow the user's language list.
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every line, newline-separated, in reading order. Empty when there was
    /// nothing to read, which is a real answer -- a fullscreen game with no
    /// HUD text says something about the screen by saying nothing.
    /// </summary>
    public static async Task<string> ReadAsync(Bitmap image)
    {
        if (Engine is null)
        {
            return string.Empty;
        }

        // Through a BMP in memory rather than pixel-poking: the OCR engine
        // wants a SoftwareBitmap in Bgra8, the capture is a GDI+ Bitmap, and
        // the decoder is the supported conversion between them. A 2048-pixel
        // screen is a few megabytes and a few milliseconds here.
        using var buffer = new MemoryStream();
        image.Save(buffer, ImageFormat.Bmp);
        buffer.Position = 0;

        using var stream = new InMemoryRandomAccessStream();
        await buffer.CopyToAsync(stream.AsStreamForWrite()).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var result = await Engine.RecognizeAsync(software);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
