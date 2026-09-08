namespace EciCas.Agents.Impulse;

/// <summary>
/// Persona drive-vector state, ported from the Python prototype's
/// Impulse.DEFAULT_VECTORS (agents/impulse/agent.py).
/// Serialized as JSON into ArchiveRecord.Content at ImpulseAgent.DrivePath.
/// </summary>
public sealed record DriveVectors(
    double Curiosity = 0.8,
    double Fatigue = 0.1,
    double Urgency = 0.0,
    double SocialDrive = 0.5,
    double Temperature = 0.4)
{
    /// <summary>Bucket edges for Expression()'s low/mid/high reads — ported verbatim from Python's _BUCKET_EDGES.</summary>
    private const double BucketLow = 0.35;
    private const double BucketHigh = 0.65;

    public DriveVectors Clamp() => new(
        Math.Clamp(Curiosity, 0.0, 1.0),
        Math.Clamp(Fatigue, 0.0, 1.0),
        Math.Clamp(Urgency, 0.0, 1.0),
        Math.Clamp(SocialDrive, 0.0, 1.0),
        Math.Clamp(Temperature, 0.0, 1.0));

    public DriveVectors Add(DriveVectors delta) => new DriveVectors(
        Curiosity + delta.Curiosity,
        Fatigue + delta.Fatigue,
        Urgency + delta.Urgency,
        SocialDrive + delta.SocialDrive,
        Temperature + delta.Temperature).Clamp();

    /// <summary>
    /// Where a drive returns to when nothing is happening to it. The record's
    /// own defaults, so there is one statement of what unmoved feels like.
    /// </summary>
    public static readonly DriveVectors Baseline = new();

    /// <summary>
    /// How much of the remaining distance to baseline a quiet turn closes.
    /// Proportional rather than a fixed step: a state knocked far out comes
    /// back quickly and one already near home barely moves, which is how
    /// settling down actually feels. At 0.2 a full Critical urgency of 0.45
    /// is back under the "alert" edge in three quiet turns and effectively
    /// home in ten.
    /// </summary>
    private const double DriftRate = 0.2;

    /// <summary>Below this a drive is at baseline, not approaching it — otherwise it halves forever and the state file never settles.</summary>
    private const double DriftSnap = 0.01;

    /// <summary>
    /// One quiet turn's worth of settling. Applied when a turn moved nothing:
    /// a persona that stays alarmed forever because one message said "urgent"
    /// is not a mood, it is a stuck gauge, and urgency in particular has no
    /// nudge that lowers it.
    ///
    /// This is not a third magnitude to tune against the other two. It is not
    /// a nudge at all -- nothing appraised anything -- it is the absence of
    /// one showing up in the state.
    /// </summary>
    public DriveVectors Drift() => new(
        Toward(Curiosity, Baseline.Curiosity),
        Toward(Fatigue, Baseline.Fatigue),
        Toward(Urgency, Baseline.Urgency),
        Toward(SocialDrive, Baseline.SocialDrive),
        Toward(Temperature, Baseline.Temperature));

    private static double Toward(double value, double baseline) =>
        Math.Abs(value - baseline) < DriftSnap ? baseline : value + (baseline - value) * DriftRate;

    /// <summary>Five drive vectors collapsed into three legible appraisal axes — fixed linear combinations, ported verbatim from Python's Impulse._axes().</summary>
    public double Alertness => Math.Clamp(Urgency - 0.3 * Fatigue, 0.0, 1.0);
    public double Warmth => Math.Clamp(0.6 * SocialDrive + 0.4 * Temperature, 0.0, 1.0);
    public double Engagement => Math.Clamp(Curiosity - 0.4 * Fatigue, 0.0, 1.0);

    /// <summary>
    /// The face this appraisal state implies — read-only, one word from the
    /// same three axes the reflex vocabulary draws from. Ported verbatim
    /// from Python's Impulse.expression(). Governance reads this when an
    /// exchange is blocked and nothing model-authored may be spoken, so
    /// what reaches the human at least matches how the persona currently
    /// feels rather than a canned error face.
    /// </summary>
    public string Expression()
    {
        if (Alertness >= BucketHigh)
        {
            return Warmth < BucketLow ? "angry" : "scared";
        }

        if (Engagement < BucketLow && Alertness < BucketLow)
        {
            return "sad";
        }

        // Raised alertness outranks warmth, which is the one place this
        // departs from the Python order. Both can be high at once — a warm
        // relationship in the middle of something urgent — and a face that
        // smiles through an emergency reads as not having heard it.
        if (Alertness >= BucketLow)
        {
            return "alert";
        }

        return Warmth >= BucketHigh ? "warm" : "neutral";
    }
}
