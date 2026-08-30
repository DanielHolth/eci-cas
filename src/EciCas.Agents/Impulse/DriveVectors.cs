namespace EciCas.Agents.Impulse;

/// <summary>
/// Persona drive-vector state, ported from the Python prototype's
/// Impulse.DEFAULT_VECTORS (agents/impulse/agent.py). Deliberately smaller
/// than the Python original: no drift-toward-baseline machinery here — see
/// gap-analysis.md, that stays a separate follow-up.
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

        if (Warmth >= BucketHigh)
        {
            return "warm";
        }

        return Alertness >= BucketLow ? "alert" : "neutral";
    }
}
