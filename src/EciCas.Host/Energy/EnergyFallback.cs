using Microsoft.Extensions.Logging;

namespace EciCas.Host.Energy;

/// <summary>
/// What running out of energy actually does: drop to the all-local tier and
/// keep talking.
///
/// Not a refusal, and not a dialog. The fiction is that Morrow is tired and
/// dumber, running on the person's own hardware — which is literally true of
/// the Free tier — so the consequence of an empty meter is a worse answer,
/// not a closed door. The surface says so in one line
/// ("[im tired and dumber now, using your local hardware to answer
/// questions]"); nothing here publishes that, because it is a display
/// concern.
///
/// It never switches back. Refilling is the person's move — see
/// POST /api/vitals/fill and the tier dropdown — because a tier that flipped
/// itself back to Pro the moment a trickle of regen landed would spend that
/// trickle on one call and drop again, several times an hour.
/// </summary>
/// <param name="onSwitched">
/// Run once, after a fallback has taken effect. The tier dropdown warms the
/// models it just pointed the agents at (see KnobsEndpoints) and this needs
/// the same thing for the same reason, except more: nobody asked for this
/// switch, so the turn that pays the cold local handshake is a turn the
/// person is already watching go wrong. Injected rather than resolved here
/// because deciding to fall back and warming a substrate are different jobs.
/// </param>
public sealed class EnergyFallback(
    TierCatalog tiers,
    ILogger<EnergyFallback> logger,
    string localTier = "Free",
    Action? onSwitched = null)
{
    private readonly Lock _gate = new();

    /// <summary>The tier an empty meter falls to.</summary>
    public string LocalTier { get; } = localTier;

    /// <summary>
    /// Called with the level after a debit. Switches once, on the edge; a
    /// no-op every other time, including every subsequent empty read.
    /// </summary>
    public void Apply(EnergyLevel level)
    {
        if (!level.IsEmpty)
        {
            return;
        }

        bool switched;
        lock (_gate)
        {
            if (string.Equals(tiers.Active, LocalTier, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            switched = tiers.Switch(LocalTier);
            if (switched)
            {
                logger.LogWarning("Energy empty — falling back from the paid tier to {Tier}.", LocalTier);
            }
        }

        // Outside the lock: the switch is already visible to every agent, and
        // a warm-up held under the gate would block the next debit for as long
        // as a 2B takes to answer.
        if (switched)
        {
            onSwitched?.Invoke();
        }
    }

    /// <summary>
    /// How full the meter has to be before a person may climb back out of the
    /// local tier. Deliberately not zero.
    /// </summary>
    public const double MinimumFractionToLeave = 0.01;

    /// <summary>
    /// Whether the surface may switch to <paramref name="target"/> right now.
    ///
    /// Falling in happens at 0%; climbing out needs 1%. The gap is the whole
    /// point — with a single threshold the meter sits exactly on the edge
    /// after a fallback, and regen puts a fraction of a cent back within
    /// seconds. That was chooseable from the dropdown: swap to Free, swap
    /// straight back to Pro, and the next prompt is answered by the paid
    /// model on a meter that is empty in every sense that matters. Repeat per
    /// turn and the budget is decorative.
    ///
    /// So this is hysteresis, not a paywall. One percent is small enough that
    /// a genuine refill clears it instantly and large enough that trickle
    /// regen has to accumulate for a while first, which is the same reason
    /// <see cref="Apply"/> never switches back on its own.
    ///
    /// The local tier is always reachable: choosing to be tired is not a
    /// privilege, and a person who wants to save energy must never be told
    /// they lack the energy to save energy.
    /// </summary>
    public bool MaySwitchTo(string target, EnergyLevel level) =>
        string.Equals(target, LocalTier, StringComparison.OrdinalIgnoreCase)
            || level.Fraction >= MinimumFractionToLeave;
}
