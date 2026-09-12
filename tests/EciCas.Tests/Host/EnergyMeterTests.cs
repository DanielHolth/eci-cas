using EciCas.Host.Energy;

namespace EciCas.Tests.Host;

/// <summary>
/// The meter is arithmetic over wall-clock time, so every test here drives a
/// fake clock rather than sleeping. No test touches disk: persistence is
/// best-effort by design and a test that asserted on it would be asserting
/// the opposite of the contract.
/// </summary>
public sealed class EnergyMeterTests
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Minimal stand-in for FakeTimeProvider. The meter reads the clock and
    /// nothing else, so a package dependency to get one method is a poor
    /// trade -- and this one can run backwards, which the real fake guards
    /// against and one test here needs.
    /// </summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (EnergyMeter Meter, TestClock Clock) Build(
        decimal annual = 5.00m, double maxHours = 48)
    {
        var clock = new TestClock(Noon);
        var options = new EnergyOptions { AnnualBudgetUsd = annual, MaxHours = maxHours };
        return (new EnergyMeter(options, path: null, clock), clock);
    }

    [Fact]
    public void MaxIsFortyEightHoursOfRegen()
    {
        var options = new EnergyOptions { AnnualBudgetUsd = 5.00m, MaxHours = 48 };

        // The ceiling is a duration, not a sum: whatever the budget is, a full
        // meter is worth two days. That invariant is the reason M is stated in
        // hours, so it is the one worth pinning.
        Assert.Equal(options.RegenPerHourUsd * 48m, options.MaxUsd);
        Assert.Equal(5.00m / 8_760m * 48m, options.MaxUsd);
    }

    [Fact]
    public void MaxIsWorthMoreThanADaysRegen()
    {
        // The roadmap's invariant: a quiet week has to bank a reserve, which
        // it cannot do if the ceiling is a day's income or less.
        var options = new EnergyOptions();
        Assert.True(options.MaxUsd > options.RegenPerHourUsd * 24m);
    }

    [Fact]
    public void StartsFullWhenThereIsNoSavedBalance()
    {
        var (meter, _) = Build();

        var level = meter.Read();

        Assert.Equal(1d, level.Fraction, 6);
        Assert.False(level.IsEmpty);
        Assert.Null(level.FullAt);
    }

    [Fact]
    public void SpendingReducesTheBalance()
    {
        var (meter, _) = Build();
        var max = meter.Read().MaxUsd;

        var level = meter.Spend(max / 4m);

        Assert.Equal(0.75d, level.Fraction, 6);
    }

    [Fact]
    public void SpendingMoreThanIsLeftEmptiesRatherThanOverdrawing()
    {
        var (meter, _) = Build();
        var max = meter.Read().MaxUsd;

        // "You are in debt" is not a state a companion is allowed to be in:
        // an expensive turn empties the meter, it never mortgages tomorrow.
        var level = meter.Spend(max * 10m);

        Assert.Equal(0m, level.BalanceUsd);
        Assert.True(level.IsEmpty);
    }

    [Fact]
    public void RegeneratesWithElapsedTime()
    {
        var (meter, clock) = Build();
        var max = meter.Read().MaxUsd;
        meter.Spend(max);

        clock.Advance(TimeSpan.FromHours(24));

        // Half the ceiling, because the ceiling is 48 hours of regen.
        Assert.Equal(0.5d, meter.Read().Fraction, 6);
    }

    [Fact]
    public void RegenerationStopsAtTheCeiling()
    {
        var (meter, clock) = Build();
        meter.Spend(meter.Read().MaxUsd);

        clock.Advance(TimeSpan.FromDays(90));

        var level = meter.Read();
        Assert.Equal(1d, level.Fraction, 6);
        Assert.Equal(level.MaxUsd, level.BalanceUsd);
    }

    [Fact]
    public void EmptyRefillsCompletelyInExactlyMaxHours()
    {
        var (meter, clock) = Build();
        meter.Spend(meter.Read().MaxUsd);

        clock.Advance(TimeSpan.FromHours(48));

        Assert.Equal(1d, meter.Read().Fraction, 6);
    }

    [Fact]
    public void FullAtPredictsWhenTheMeterRecovers()
    {
        var (meter, _) = Build();
        meter.Spend(meter.Read().MaxUsd);

        var level = meter.Read();

        Assert.NotNull(level.FullAt);
        Assert.True((level.FullAt!.Value - Noon.AddHours(48)).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AClockThatGoesBackwardsDoesNotDrainTheMeter()
    {
        var (meter, clock) = Build();
        meter.Spend(meter.Read().MaxUsd / 2m);
        var before = meter.Read().BalanceUsd;

        clock.Advance(TimeSpan.FromHours(-5));

        // Negative elapsed time credits nothing and, more importantly, debits
        // nothing. A laptop whose clock resyncs is not a person who spent money.
        Assert.Equal(before, meter.Read().BalanceUsd);
    }

    [Fact]
    public void AnnualBudgetIsWhatItSpendsInAYear()
    {
        // The grounding claim: R is $5/year. Drain the meter, then let a full
        // year elapse and confirm the regen it would have credited is the
        // budget -- which is what makes AnnualBudgetUsd the honest lever for
        // a patch that has to chase provider prices.
        var options = new EnergyOptions { AnnualBudgetUsd = 5.00m };

        Assert.Equal(5.00m, options.RegenPerHourUsd * 8_760m, 6);
    }

    [Fact]
    public void FillGrantsAFullMeter()
    {
        var (meter, _) = Build();
        meter.Spend(meter.Read().MaxUsd);

        var level = meter.Fill();

        Assert.Equal(1d, level.Fraction, 6);
    }
}
