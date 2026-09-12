namespace EciCas.Host.Energy;

/// <summary>
/// The two dials behind Morrow's energy, and nothing else.
///
/// Energy is a token bucket denominated in the same US dollars the substrate
/// layer already reports per call, so the meter spends what the providers
/// actually charge rather than a second, parallel notion of "a request".
/// That is the whole reason it is money and not turns: a turn on a long
/// window costs several times a turn on a short one, and a bucket counting
/// turns would be generous to exactly the sessions that cost the most.
///
/// What a person sees is a percentage. What this class holds is the cost
/// ceiling that percentage is a fraction of.
/// </summary>
public sealed class EnergyOptions
{
    /// <summary>
    /// What one person's inference is allowed to cost us in a year, in USD.
    /// This is the rate dial R, expressed as the business constraint it
    /// actually is rather than as a refill-per-hour nobody can sanity-check.
    ///
    /// It is the only number here that sets annual exposure, which makes it
    /// the lever a patch reaches for when provider prices move. That is
    /// safe to do precisely because empty now falls back to Local: turning
    /// this down slows the remote tiers, it does not strand anyone.
    /// </summary>
    public decimal AnnualBudgetUsd { get; set; } = 5.00m;

    /// <summary>
    /// The ceiling M, expressed as hours of regeneration rather than as a
    /// sum of money.
    ///
    /// Stating it as a duration is what keeps the two dials honest: M has to
    /// be worth more than a day's regen or a quiet week banks nothing, and
    /// at 48 hours that invariant is visible in the number itself. It also
    /// means <see cref="AnnualBudgetUsd"/> can be retuned without silently
    /// changing what a full meter is worth relative to a day.
    ///
    /// Two days is the smallest ceiling that survives a weekend.
    /// </summary>
    public double MaxHours { get; set; } = 48;

    /// <summary>
    /// Where the balance is kept, relative to the binary. Empty means the
    /// meter starts full every run — which is what the mock and local tiers
    /// want, since neither spends anything a bucket needs to ration.
    /// </summary>
    public string Path { get; set; } = "energy.json";

    /// <summary>
    /// Regeneration per hour, derived. A year is 8,760 hours; the budget is
    /// spread flat across them.
    ///
    /// Flat is deliberate for now. The roadmap wants recovery weighted
    /// towards overnight — better fiction, and it smooths provider load off
    /// peak — but weighting redistributes this rate rather than changing it,
    /// so it can arrive later without moving any number here.
    /// </summary>
    public decimal RegenPerHourUsd => AnnualBudgetUsd / 8_760m;

    /// <summary>
    /// The ceiling in dollars: <see cref="MaxHours"/> of regeneration.
    /// </summary>
    public decimal MaxUsd => RegenPerHourUsd * (decimal)MaxHours;
}
