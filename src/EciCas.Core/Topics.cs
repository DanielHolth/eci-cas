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

    /// <summary>
    /// Facts the Archivist pulled out of a turn, before they have an address.
    /// Extraction and filing are two different judgements — what was stated,
    /// and where it belongs — so they are two agents on two calls, and this
    /// is the seam. Published on every turn, including the ones that stated
    /// nothing, because the write batch counts turns rather than facts.
    /// </summary>
    public const string Facts = "events.facts";
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
