using System.Text.RegularExpressions;
using EciCas.Core;

namespace EciCas.Agents.Security;

/// <summary>
/// One rule. Fires when (any of AnyOf matches) AND (all of AllOf match) AND
/// (none of Unless matches). A rule with neither AnyOf nor AllOf matches
/// nothing — an empty rule is a mistake, and the safe reading of a mistake
/// here is "does not fire," never "fires on everything."
/// </summary>
public sealed class SecurityRule
{
    public required string Id { get; init; }
    public required Verdict Verdict { get; init; }
    public required string Concern { get; init; }
    public IReadOnlyList<Regex> AnyOf { get; init; } = [];
    public IReadOnlyList<Regex> AllOf { get; init; } = [];
    public IReadOnlyList<Regex> Unless { get; init; } = [];

    public bool Matches(string text)
    {
        if (AnyOf.Count == 0 && AllOf.Count == 0)
        {
            return false;
        }

        if (AnyOf.Count > 0 && !AnyOf.Any(pattern => pattern.IsMatch(text)))
        {
            return false;
        }

        if (AllOf.Count > 0 && !AllOf.All(pattern => pattern.IsMatch(text)))
        {
            return false;
        }

        return !Unless.Any(pattern => pattern.IsMatch(text));
    }
}
