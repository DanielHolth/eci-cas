namespace EciCas.Core;

/// <summary>
/// How much of a picture the model is asked to look at.
///
/// Not a quality setting — a price. The vendor fits a low-detail image
/// inside 512x512 and a high-detail one inside 2048x2048, and bills the
/// patches that result: roughly 173 tokens against roughly 2,800. Sixteen
/// times the cost, for the same screenshot.
///
/// What the sixteen times buys is text. At 512 a screen is a scene — a dark
/// corridor, a door ajar, a health bar nearly empty — and every label on it
/// is three pixels tall. That is enough for most of what someone asks their
/// companion about a game, and it is why Low is the default: a look that
/// costs three hundredths of a cent can be taken on every turn without
/// anyone deciding it was worth taking.
/// </summary>
public enum ImageDetail
{
    Low,
    High,
}

/// <param name="Bytes">The encoded image. Sent as a data URL, so nothing on
/// disk has to be reachable by the vendor.</param>
/// <param name="MediaType">"image/jpeg", "image/png" — whatever the bytes are.</param>
public sealed record SubstrateImage(byte[] Bytes, string MediaType, ImageDetail Detail)
{
    public string DataUrl() => $"data:{MediaType};base64,{Convert.ToBase64String(Bytes)}";

    public SubstrateImage At(ImageDetail detail) => this with { Detail = detail };
}
