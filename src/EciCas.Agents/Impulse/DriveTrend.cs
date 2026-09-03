namespace EciCas.Agents.Impulse;

/// <summary>
/// Turns a run of recorded drive states into a sentence about how the
/// persona has been moving, for Reflection to think with.
///
/// A level is a gauge; a trajectory is a history. "Engagement 0.9" tells
/// Reflection nothing it can write an honest note about — every turn would
/// read the same way, and a model handed a bare number will invent a story
/// to explain it. "Engagement rising while alertness stays flat" is a claim
/// about something that actually happened, which is the whole difference
/// between the persona reporting a state and performing one.
///
/// Described in the three appraisal axes and Expression()'s vocabulary
/// rather than the five raw vectors, for the same reason Expression exists:
/// a persona reciting "my Curiosity parameter is 0.85" is grounded and
/// still wrong — it is telemetry in a voice, not a thought. Words in, words
/// out, and no number reaches a prompt.
/// </summary>
public static class DriveTrend
{
    /// <summary>
    /// How far an axis must move across the window to count as a direction
    /// rather than noise. Impulse nudges are small and frequent, so a
    /// threshold near zero would call every window "rising".
    /// </summary>
    private const double Meaningful = 0.1;

    /// <summary>
    /// <paramref name="newestFirst"/> is the store's natural order — it
    /// scans the append-only file backwards, so index 0 is now and the last
    /// element is the oldest state still retained.
    /// </summary>
    public static string Describe(IReadOnlyList<DriveVectors> newestFirst)
    {
        // One state is a level, not a trend, and saying so is more useful to
        // Reflection than a sentence implying movement nobody measured.
        if (newestFirst.Count == 0)
        {
            return "No drive states recorded yet.";
        }

        var now = newestFirst[0];
        if (newestFirst.Count == 1)
        {
            return $"One drive state recorded, no history to compare: {now.Expression()}.";
        }

        var then = newestFirst[^1];
        var moves = new[]
        {
            Direction("engagement", now.Engagement - then.Engagement),
            Direction("alertness", now.Alertness - then.Alertness),
            Direction("warmth", now.Warmth - then.Warmth),
        };

        var face = now.Expression() == then.Expression()
            ? $"{now.Expression()} throughout"
            : $"{then.Expression()} then, {now.Expression()} now";

        return $"Across the last {newestFirst.Count} recorded states: {string.Join(", ", moves)}. {char.ToUpperInvariant(face[0])}{face[1..]}.";
    }

    private static string Direction(string axis, double delta) => delta switch
    {
        > Meaningful => $"{axis} rising",
        < -Meaningful => $"{axis} falling",
        _ => $"{axis} steady",
    };
}
