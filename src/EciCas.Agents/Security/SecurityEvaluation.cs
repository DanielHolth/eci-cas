using EciCas.Core;

namespace EciCas.Agents.Security;

/// <summary>What the rule engine decided, and enough to audit why.</summary>
public sealed record SecurityEvaluation(Verdict Verdict, string Concern, IReadOnlyList<string> MatchedRuleIds);
