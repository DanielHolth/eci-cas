namespace EciCas.Shell;

/// <summary>
/// The voice key's half of the shell's configuration: what listens, and for
/// how long it is willing to.
/// </summary>
internal sealed class DictationOptions
{
    /// <summary>Off means the key still lights the face and nothing is ever
    /// recorded -- which is the only way to be certain, rather than to be
    /// told, that a microphone is not open.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A whisper.cpp GGML model, by a path relative to the repository root.
    /// Not committed and not downloaded by a launch: `scripts/get-whisper-model.ps1`
    /// fetches it, and without it the key says so instead of listening.
    ///
    /// The multilingual `base` rather than `base.en`, at the same size, because
    /// this household speaks two languages at the persona and the archive keeps
    /// Norwegian facts. `small` is the upgrade worth making if Norwegian
    /// dictation is the main use.
    /// </summary>
    public string ModelPath { get; set; } = "models/whisper/ggml-base.bin";

    /// <summary>
    /// Words the decoder should expect to hear, as a sentence handed to
    /// whisper before the take.
    ///
    /// Whisper decodes towards what is likely, and "Morrow" is not: spoken
    /// with a Norwegian accent it lands on Morrow, Marrow, Morro, Moro or
    /// Morau, all of which are more probable English than a name the model
    /// has never met. An initial prompt is the supported way to say
    /// otherwise -- it is not a grammar and it forbids nothing, it just moves
    /// the odds, and a handful of proper nouns is the whole of what it is
    /// good for. Keep it short: it is prepended to every take, and a long one
    /// starts colouring the transcript with its own phrasing.
    ///
    /// Empty switches the bias off.
    /// </summary>
    public string Vocabulary { get; set; } = "Morrow";

    /// <summary>An ISO code to pin the language, or "auto" to let the model
    /// decide per take -- which is what a bilingual speaker wants, at the cost
    /// of the occasional take understood in the wrong one.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// How long the key must be held before the microphone opens at all.
    ///
    /// This is the direct consequence of the keys being watched rather than
    /// claimed: the voice key is a hyphen that still types, so typing one in
    /// chat arrives here as a press. A tap is not a held key, so nothing opens,
    /// nothing is transcribed, and the face does not even flicker.
    /// </summary>
    public int HoldMs { get; set; } = 350;

    /// <summary>A ceiling on one take, in case the key is stuck down or the
    /// person walked away mid-sentence. The take is transcribed rather than
    /// thrown away when it trips.</summary>
    public int MaxSeconds { get; set; } = 60;

    /// <summary>
    /// The loudest sample a take may contain and still be called silence, 0..1.
    ///
    /// Not an optimisation. Whisper asked to transcribe a room answers with
    /// something -- "Thank you.", a subtitle credit, whatever that silence
    /// resembles -- and a persona that replies to an accidental keypress is
    /// worse than one that says it heard nothing. The gate is what makes
    /// "nothing was said" a state this can be in.
    /// </summary>
    public double SilenceFloor { get; set; } = 0.012;
}
