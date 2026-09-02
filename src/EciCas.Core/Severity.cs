namespace EciCas.Core;

/// <summary>
/// OR-upscale-only: severity on a bundle is the max of every advisory's severity,
/// never a downgrade. Impulse's reflex path is capped at Elevated — only
/// Perception/Librarian may tag Critical.
/// </summary>
public enum Severity
{
    Restful = 0,
    Neutral = 1,
    Elevated = 2,
    Critical = 3,
}

public static class SeverityExtensions
{
    public static Severity Max(this Severity a, Severity b) => a > b ? a : b;

    public static Severity MaxOf(IEnumerable<Severity> severities) =>
        severities.Aggregate(Severity.Restful, (max, s) => max.Max(s));
}
