namespace EciCas.Agents.Toolkit;

/// <summary>
/// How long a toolkit may run before <see cref="ToolkitHandlerAgent"/> gives
/// up on it and cancels the process. A script that hangs (waiting on a
/// prompt it will never get, or on a network call that never returns) would
/// otherwise block the handler's queue -- one worker, one script at a time --
/// for the rest of the session.
/// </summary>
public sealed class ToolkitOptions
{
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether ToolkitManagerAgent may route a turn to a toolkit or report
    /// one back at all. Off on Free/Budget -- a toolkit that can run
    /// arbitrary PowerShell is not something a cost-free tier should be able
    /// to reach silently off a keyword match. On means the fan-out
    /// participates; off means the agent no-ops on every turn, same as if it
    /// were never registered.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The riskiest capability a manifest may use on this tier. Lets a tier
    /// that must not run code on the machine still search: Budget is Network.
    /// </summary>
    public CapabilityRisk MaxRisk { get; set; } = CapabilityRisk.System;

    /// <summary>
    /// Minimum cosine similarity between a turn and a toolkit's best trigger
    /// exemplar before ToolkitManagerAgent will dispatch to it. Below this,
    /// silence is the correct answer far more often than a wrong toolkit
    /// call is -- this is an unattended background match, not a considered
    /// reply Intent can revise.
    /// </summary>
    public double RouteFloor { get; set; } = 0.87;

    /// <summary>
    /// How far the best toolkit must lead the runner-up. A message that is
    /// really about nothing scores within a hair of two toolkits at once
    /// (measured: "How are you feeling today?" 0.863 search, 0.851 guide),
    /// while a real ask leads by 0.05 or more. Ambiguity means no toolkit
    /// runs -- Morrow answers from what she knows instead of sending an
    /// unrelated toolkit's output back into the conversation.
    /// </summary>
    public double RouteMargin { get; set; } = 0.03;
}
