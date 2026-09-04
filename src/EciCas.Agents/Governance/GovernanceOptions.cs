namespace EciCas.Agents.Governance;

/// <summary>
/// Adding an advisor is a config line here plus the advisor's own class+DI
/// registration — never an edit to GovernanceAgent. Empty roster (no
/// advisory agents exist yet) means a bundle completes as soon as the
/// originating perception event arrives.
/// </summary>
public sealed class GovernanceOptions
{
    public string[] BundleRoster { get; set; } = [];
    public int BundleTimeoutMs { get; set; } = 4000;

    /// <summary>How many Intent revision passes a Yellow verdict buys before Governance proceeds to Action anyway.</summary>
    public int MaxRevisionPasses { get; set; } = 1;

    /// <summary>
    /// How long after a bundle is published Governance keeps waiting for the
    /// verdict that closes it. Only the unhappy path reaches this: a verdict
    /// retires its own bundle, so this is what stops a turn that died
    /// somewhere between Intent and Security from occupying memory for the
    /// life of the process. Generous on purpose — it is an abandonment
    /// threshold, not a deadline, and expiring a turn that was merely slow
    /// would turn one late reply into two.
    /// </summary>
    public int BundleAbandonMs { get; set; } = 120_000;
}
