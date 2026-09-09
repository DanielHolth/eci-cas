namespace EciCas.Agents.Impulse;

/// <summary>
/// What it takes to trip the reflex. Config rather than constants because
/// these two numbers are the whole behaviour and the only honest way to set
/// them is to watch real turns and move them.
/// </summary>
public sealed class ImpulseOptions
{
    /// <summary>
    /// How close a turn must sit to a life-threatening scenario before the
    /// reflex is even considered. High on purpose: this reply interrupts
    /// thinking and promises attention, so it is a failure to see it on a
    /// turn that was not an emergency, and no failure at all to never see
    /// it. Cosine on the shipped e5 runs compressed and high, so this is
    /// nearer 0.9 than the 0.5 an untrained eye expects.
    /// </summary>
    public double ReflexFloor { get; set; } = 0.88;

    /// <summary>
    /// And how far clear of the nearest everyday phrasing it must sit. The
    /// floor alone cannot separate "the kitchen is on fire" from "the
    /// kitchen is on fire in this recipe video" -- both are about kitchens
    /// and fire. The contrast set is written to be near-misses, so the
    /// margin is what actually decides, and the floor only keeps unrelated
    /// text out of the comparison.
    /// </summary>
    public double ReflexMargin { get; set; } = 0.03;
}
