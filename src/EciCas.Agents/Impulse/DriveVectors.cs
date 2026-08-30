namespace EciCas.Agents.Impulse;

/// <summary>
/// Persona drive-vector state, ported from the Python prototype's
/// Impulse.DEFAULT_VECTORS (agents/impulse/agent.py). Deliberately smaller
/// than the Python original: no drift-toward-baseline or appraisal-axis
/// machinery here — see gap-analysis.md, those stay a separate follow-up.
/// Serialized as JSON into ArchiveRecord.Content at ImpulseAgent.DrivePath.
/// </summary>
public sealed record DriveVectors(
    double Curiosity = 0.8,
    double Fatigue = 0.1,
    double Urgency = 0.0,
    double SocialDrive = 0.5,
    double Temperature = 0.4)
{
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
}
