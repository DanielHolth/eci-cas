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
public sealed class EnergyFallback(TierCatalog tiers, ILogger<EnergyFallback> logger, string localTier = "Free")
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

        lock (_gate)
        {
            if (string.Equals(tiers.Active, LocalTier, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (tiers.Switch(LocalTier))
            {
                logger.LogWarning("Energy empty — falling back from the paid tier to {Tier}.", LocalTier);
            }
        }
    }
}
