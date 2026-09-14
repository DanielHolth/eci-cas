using EciCas.Core;

namespace EciCas.Agents.Sight;

/// <summary>
/// What Sight is allowed to look at, and how hard. Config rather than code
/// because every number here is a thing a person might reasonably want
/// different -- and because one of them spends money.
/// </summary>
public sealed class SightOptions
{
    /// <summary>
    /// Off means the screenshot is still taken and still read by the local
    /// OCR, and nothing is ever sent anywhere. The picture leaves this
    /// machine only when this is true and a provider with eyes is configured
    /// for "Sight" -- two switches, not one.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// What a look costs by default. Low fits the screen into 512 pixels for
    /// 173 tokens, which is enough to say what kind of thing is on screen and
    /// nowhere near enough to read a label -- the local OCR covers that half
    /// for nothing. High resizes to 2048 for sixteen times the price.
    ///
    /// Low is the default everywhere including Premium, because looking
    /// cheaply every turn and paying for the close look on the turns that ask
    /// for it is cheaper than always paying, right up until nine turns in ten
    /// need the close look.
    /// </summary>
    public ImageDetail Detail { get; set; } = ImageDetail.Low;

    /// <summary>
    /// How long the turn waits for a look that was started when the
    /// microphone opened. Usually zero wait -- the person spent a second or
    /// two speaking and the call ran through it -- so this is the ceiling on
    /// the unusual case, kept well under Governance's bundle timeout so a
    /// slow look degrades to a blind turn rather than stalling the reply.
    /// </summary>
    public int CollectMs { get; set; } = 6000;

    /// <summary>
    /// Phrases that buy the closer look. Config, not code: two languages are
    /// spoken at this machine and a list of English verbs would leave half
    /// the questions about the screen on the cheap pass.
    /// </summary>
    public string[] CloserPhrases { get; set; } =
    [
        "screen", "skjerm", "read this", "les dette", "what does it say", "hva står det",
        "this text", "denne teksten", "look at", "se på", "what am i looking at",
    ];

    /// <summary>
    /// Phrases that mean "read it to me" rather than "have a look". The
    /// difference matters enough to be its own list: a reading is spoken past
    /// the persona's sentence budget, because a person who cannot see their
    /// screen is owed the screen and not a summary of it.
    /// </summary>
    public string[] ReadPhrases { get; set; } =
    [
        "read the screen", "read my screen", "read this to me", "read it to me",
        "read it out", "read out loud", "what does it say", "read everything",
        "les skjermen", "les dette for meg", "les opp", "hva står det",
    ];

    /// <summary>
    /// What the cheap pass writes on its own last line when it could not make
    /// out something it thinks matters. The instruction file teaches the
    /// word; this is only where the two agree on it.
    /// </summary>
    public string CloserMarker { get; set; } = "NEED-A-CLOSER-LOOK";

    /// <summary>Ceiling on the description handed to Intent. A paragraph about
    /// the screen crowding out six turns of what the person actually said is
    /// the failure mode this prevents.</summary>
    public int AdviceChars { get; set; } = 600;

    /// <summary>Ceiling on the OCR transcript. A busy IDE reads out at several
    /// thousand characters; the full text stays on disk beside the
    /// screenshot, which is the source of truth if it is ever wanted.</summary>
    public int WordsChars { get; set; } = 1200;
}
