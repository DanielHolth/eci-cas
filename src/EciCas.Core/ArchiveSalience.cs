namespace EciCas.Core;

/// <summary>
/// What a row is worth *now*, as opposed to what it was worth when it was
/// written.
///
/// Importance is scored once, at write time, by an explicit priority list —
/// so a fact that mattered in March outranks one that matters today, forever,
/// and the fan-out budget is spent on it. This is the read-side correction,
/// and it is only ever a comparison key: nothing is deleted, nothing is
/// rewritten, and the number on disk is untouched. The capsule cares what is
/// stored, the persona cares what surfaces, and they are allowed to differ.
///
/// Three terms, in the order they were reasoned about:
///
/// **Age decays it.** Exponential with a half-life, from the last time the
/// row was touched. Exponential rather than linear because a linear rule has
/// to pick an age at which a fact is worth nothing, and there is no such age.
///
/// **Being read counts as being touched.** Decay runs from the later of the
/// row's timestamp and its last hit, so a fact from last year that keeps
/// being recalled never goes stale. That is the difference between
/// forgetting and merely ageing.
///
/// **Hits add.** Not multiply: a row nobody has asked for yet must not be
/// zeroed for it, and a young archive where nothing has been hit has to rank
/// exactly as it does today. The count is read as a rate against turns
/// recorded, so a row hit in a tenth of all turns scores the same on day two
/// and in year three.
///
/// Off is a supported setting, and it is what a fresh archive should probably
/// run: a half-life of zero leaves Importance exactly as written.
/// </summary>
public static class ArchiveSalience
{
    /// <summary>
    /// Ceiling on the hit term. A rate can reach 1.0 — one row recalled on
    /// every single turn — and without a cap the weight would have to be set
    /// small enough that the term never mattered for anything else.
    /// </summary>
    public const double MaxHitRate = 1.0;

    public static double Effective(
        ArchiveRecord record,
        DateTimeOffset now,
        long turnsRecorded,
        double halfLifeDays,
        double hitWeight)
    {
        var touched = record.LastHitAt is { } hit && hit > record.Timestamp ? hit : record.Timestamp;
        var age = (now - touched).TotalDays;

        // A negative age is a clock that went backwards or a row written a
        // moment ago by a machine whose UTC differs by a second. Neither is
        // a reason to hand out a bonus, so undecayed is the floor.
        var decayed = halfLifeDays <= 0 || age <= 0
            ? record.Importance
            : record.Importance * Math.Pow(0.5, age / halfLifeDays);

        return decayed + hitWeight * HitRate(record, turnsRecorded);
    }

    /// <summary>
    /// Share of recorded turns that recalled this row. Zero turns means an
    /// archive nothing has been asked of yet, not a row with a rate of zero.
    /// </summary>
    public static double HitRate(ArchiveRecord record, long turnsRecorded) =>
        turnsRecorded <= 0 || record.Hits <= 0
            ? 0.0
            : Math.Min(MaxHitRate, record.Hits / (double)turnsRecorded);
}
