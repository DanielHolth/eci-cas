using EciCas.Host.Energy;

namespace EciCas.Tests.Host;

/// <summary>
/// The boundary has two thresholds on purpose, and the gap between them is
/// the only thing stopping the meter from being decorative. These tests are
/// about the gap.
/// </summary>
public sealed class EnergyFallbackTests
{
    /// <summary>
    /// MaySwitchTo is a decision about a number and a name; it reads neither
    /// collaborator. Handing it nulls keeps that true — if someone later
    /// makes the rule depend on the catalog, this stops compiling or throws,
    /// which is the right moment to find out.
    /// </summary>
    private static EnergyFallback Fallback() =>
        new(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<EnergyFallback>.Instance, localTier: "Free");

    private static EnergyLevel At(double fraction) =>
        new((decimal)fraction, 1m, fraction, null);

    /// <summary>
    /// The exploit, written down. Falling in happens at 0%; regen puts a
    /// fraction of a cent back within seconds of landing. If climbing out
    /// were also allowed at anything above nothing, the dropdown would hand
    /// back a paid answer on an empty budget once per turn, forever.
    /// </summary>
    [Fact]
    public void JustAboveEmpty_CannotBuyItsWayBackToAPaidTier()
    {
        var fallback = Fallback();

        Assert.False(fallback.MaySwitchTo("Pro", At(0.000_1)));
        Assert.False(fallback.MaySwitchTo("Pro", At(0.009)));
        Assert.True(fallback.MaySwitchTo("Pro", At(EnergyFallback.MinimumFractionToLeave)));
    }

    /// <summary>
    /// Choosing to be tired is not a privilege. A person who wants to stop
    /// spending must never be told they lack the energy to stop spending —
    /// so the local tier is reachable from anywhere, including from itself.
    /// </summary>
    [Fact]
    public void TheLocalTier_IsReachableOnAnEmptyMeter()
    {
        var fallback = Fallback();

        Assert.True(fallback.MaySwitchTo("Free", At(0)));
        // Casing comes from the dropdown's option value, not from a human
        // typing, but the comparison is the preset's own all the same.
        Assert.True(fallback.MaySwitchTo("free", At(0)));
    }

    /// <summary>A full meter is not a special case; it is simply over the line.</summary>
    [Fact]
    public void AFilledMeter_MayGoAnywhere()
    {
        var fallback = Fallback();

        Assert.True(fallback.MaySwitchTo("Pro", At(1.0)));
    }
}
