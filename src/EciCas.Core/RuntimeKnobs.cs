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
    private int _recallThreads = 3;
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

    /// <summary>
    /// How many rows one Recall picking call may hand back — overrides
    /// RecallOptions.MaxPickedPerWorker live, and by extension decides how
    /// wide the cosine cut ahead of it is (see <see cref="VectorCandidates"/>),
    /// and it applies to every lane the thread count opens.
    ///
    /// Ten, not twenty: past ten rows out of one pair the addition is
    /// prompt weight rather than grounding.
    /// </summary>
    public int RecallDepth
    {
        get => _recallDepth;
        set => _recallDepth = Math.Clamp(value, 1, 10);
    }

    /// <summary>
    /// How many recalls a turn may fire, the recency lane included. One is
    /// the lane alone — no pair is opened at all — and every thread after it
    /// is one pair, alternating: the vector lane first, then the selector's,
    /// so a turn with two threads spends its one pair on the cheap lead
    /// rather than on a substrate call's opinion.
    ///
    /// The count a person actually reasons about is calls per turn, which is
    /// why this is the knob rather than the two pair budgets it drives:
    /// Librarian's lane caps are <see cref="VectorLanePairs"/> and
    /// <see cref="SelectorLanePairs"/>, and they exist to be read, not set.
    /// </summary>
    public int RecallThreads
    {
        get => _recallThreads;
        set => _recallThreads = Math.Clamp(value, 1, 12);
    }

    /// <summary>Pairs the vector lane — passage leads and gloss matches
    /// together — may contribute. Takes the odd one, so threads 2 is a
    /// vector lead and nothing else.</summary>
    public int VectorLanePairs => _recallThreads / 2;

    /// <summary>Pairs the selector call may name.</summary>
    public int SelectorLanePairs => (_recallThreads - 1) / 2;

    /// <summary>
    /// How many rows survive the cosine cut inside one pair: the depth
    /// itself, so the cut is the answer rather than a shortlist for a call
    /// that would only cut it again. This applies to every pair Recall
    /// opens, not only the ones the gloss sweep found -- the cut runs
    /// downstream of selection and does not know how a pair was chosen.
    ///
    /// Being equal to the depth is also what makes the picking call
    /// disappear: a lane holding no more rows than it may return has
    /// nothing left to pick, so a narrowed pair costs no substrate call at
    /// all. The measured reason to want that is the bench's nopick rate --
    /// 78% against a lenient bar -- which said the picker was the read
    /// side's largest loss, and cosine-ranked rows are exactly the ones it
    /// was discarding.
    /// </summary>
    public int VectorCandidates => _recallDepth;

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

