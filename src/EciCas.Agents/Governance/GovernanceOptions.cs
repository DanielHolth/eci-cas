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
}
