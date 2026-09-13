namespace EciCas.Core;

/// <summary>
/// Work the host does to its own derived stores, configured outside the
/// tiers on purpose.
///
/// A tier says what the persona thinks with. This says what repairs the
/// index, and the two must not be the same dial: the Free tier exists to be
/// answerable with no vendor key in play, so a tier file that named a
/// hosted model here would make Free a tier that reaches the network. The
/// rebuild is not the persona speaking -- nobody is waiting on it, it runs
/// once at boot, and it is the one place where paying for a better model is
/// obviously worth it, because what it writes is read by every turn after.
///
/// So the entry lives here and is installed into every tier's agent table
/// by the loader. Whichever tier booted, the rebuild calls the same model.
/// </summary>
public sealed class MaintenanceOptions
{
    /// <summary>
    /// The name the rebuild's substrate entry is installed under. Not an
    /// agent: nothing subscribes to it and no turn calls it.
    /// </summary>
    public const string RebuildAgentName = "rebuild";

    /// <summary>
    /// Who re-reads the archive at boot. Null means no rebuild is
    /// configured, which is the ordinary state of a host that has never
    /// been pointed at a better model, and is a silent skip rather than a
    /// warning.
    /// </summary>
    public SubstrateAgentEntry? Rebuild { get; set; }

    /// <summary>
    /// Model ids whose work the rebuild is allowed to throw away, alongside
    /// the rows that name no model at all (see <see cref="Fact.WrittenBy"/>).
    ///
    /// This is what makes the pass converge: rows the rebuild writes carry
    /// its own model id, which is not in this list, so the second run finds
    /// nothing. It is also the whole safety argument -- a list that named
    /// the rebuild's own model would re-read the archive forever, and one
    /// that is empty still catches every row written before the column
    /// existed.
    /// </summary>
    public List<string> Replaces { get; set; } = [];

    /// <summary>
    /// Puts the rebuild's entry into an agent table, so the substrate
    /// registry resolves it like any other consumer -- same provider, same
    /// circuit breaker, same cost accounting. Called once per tier rather
    /// than once, because a tier switch replaces the whole table by
    /// reference and a rebuild installed only into the booted tier would
    /// vanish the first time somebody moved the dropdown.
    ///
    /// Installed after a tier's missing-key check on purpose: the rebuild's
    /// key is not Free's business, and counting it would grey out the one
    /// tier whose promise is that it needs no key.
    /// </summary>
    public void Install(SubstrateOptions substrates)
    {
        if (Rebuild is not null)
        {
            substrates.Agents[RebuildAgentName] = Rebuild;
        }
    }
}
