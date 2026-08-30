namespace EciCas.Core;

/// <summary>
/// Deterministic MVP heuristic, not a substrate call: significant words (5+
/// letters) from a piece of text, lowercased and deduped. Shared by Reasoning
/// (proposes Recall lookup paths) and Consolidator (writes under those same
/// paths) so a write and a later read of the same content land on the same
/// keys — see plan §3.4.
/// </summary>
public static class SignificantWords
{
    public static string[] Extract(string text, int take = 3) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetter).ToArray()).ToLowerInvariant())
            .Where(word => word.Length >= 5)
            .Distinct()
            .Take(take)
            .ToArray();
}
