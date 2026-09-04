namespace EciCas.Core;

/// <summary>
/// Topics are named by purpose, never by recipient — no agent names another
/// agent. See docs/architecture.md for the full roster/topic table.
/// </summary>
public static class Topics
{
    /// <summary>Wildcard subscription for ArchiveLogger and console — every envelope, zero coupling.</summary>
    public const string All = "*";

    public const string Perception = "events.perception";
    public const string Advisories = "events.advisories";
    public const string Proposal = "events.proposal";
    public const string SelectedPairs = "events.selected-pairs";
    public const string Bundle = "events.bundle";
    public const string Action = "events.action";
    public const string Conclusion = "events.conclusion";
    public const string Verdict = "events.verdict";
    public const string SystemControl = "system.control";

    /// <summary>
    /// One envelope per substrate call, derived from whatever triggered it.
    /// Not an event of cognition — nothing downstream decides anything on
    /// it — so it sits beside system.control rather than under events.*.
    /// </summary>
    public const string Telemetry = "system.telemetry";
}
