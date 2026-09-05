namespace EciCas.Core;

/// <summary>
/// Live-tunable numbers the companion UI's Debug panel exposes as sliders —
/// a session experiment a person can nudge without a restart or an
/// appsettings edit. Deliberately in-memory only and deliberately not an
/// instruction file: these are numbers (and one closed vocabulary), not
/// prose, so they have no business in `instructions/*.txt` (see AGENTS.md
/// "Instructions are prose, not code"). A restart resets every knob to its
/// default, which matches the tier's own static config default so an
/// untouched slider changes nothing.
/// </summary>
public sealed class RuntimeKnobs
{
    private int _maxSentences = 2;
    private int _reflectionEvery = 5;
    private int _recallDepth = 5;
    private Mood _mood = Mood.Neutral;

    /// <summary>Upper bound Intent is told to keep replies within, clamped
    /// to the slider's own range so a bad request can't ask for zero or a
    /// wall of text.</summary>
    public int MaxSentences
    {
        get => _maxSentences;
        set => _maxSentences = Math.Clamp(value, 1, 20);
    }

    /// <summary>How many concluded turns Reflection accumulates before it
    /// scores a batch — overrides ReflectionOptions.BatchSize live.</summary>
    public int ReflectionEvery
    {
        get => _reflectionEvery;
        set => _reflectionEvery = Math.Clamp(value, 1, 20);
    }

    /// <summary>How many rows one Recall picking call may hand back —
    /// overrides RecallOptions.MaxPickedPerWorker live.</summary>
    public int RecallDepth
    {
        get => _recallDepth;
        set => _recallDepth = Math.Clamp(value, 1, 20);
    }

    /// <summary>How the persona feels this turn, on top of whatever
    /// Identity's own advisory already says about who it is.
    ///
    /// Mood, not tone: tone is a property of the prose, so "answer in a
    /// maleficent tone" asks for a register. These five are states of the
    /// speaker, and a speaker in a state chooses different words, volunteers
    /// different things and declines to soften different ones -- which is
    /// what the slider was always reaching for. It also stops this aside
    /// contradicting Identity's, which describes standing character.</summary>
    public Mood Mood
    {
        get => _mood;
        set => _mood = value;
    }
}

/// <summary>
/// Five-step dial from cruel to effusive. A closed enum rather than free
/// text: the slider has five positions and the prompt bracket it produces
/// (see IntentAgent.AppendMood) has to be one of exactly these words.
/// </summary>
public enum Mood
{
    Maleficent,
    Sarcastic,
    Neutral,
    Helpful,
    Ecstatic,
}

