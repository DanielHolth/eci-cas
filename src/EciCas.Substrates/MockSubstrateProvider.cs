using System.Text.RegularExpressions;
using EciCas.Core;

namespace EciCas.Substrates;

/// <summary>
/// Zero-cost, zero-dependency substrate for the "mock" budget tier — every
/// substrate class resolves here until a manifest entry opts into "live".
///
/// Echoing the prompt back is enough for the agents that just need *a*
/// string, but not for the two that ask a numbered question: Librarian
/// picking archive pairs and Recall picking rows both enumerate candidates
/// as "0. …", "1. …" and expect bare index numbers back. An echo parses to
/// nothing there, so the whole knowledge swarm silently took its empty path
/// on the free tier and could never be exercised without spending money.
/// The mock answers those with the first index instead. It keys off the
/// enumerated-candidate *format* the two prompts share, not their wording,
/// so rephrasing either prompt doesn't break it.
///
/// Everything else echoes the prompt's LAST line, minus the bracketed
/// asides on the end of it. A whole prompt is a wall of rules and recalled
/// context, and it lands in a speech bubble on the companion surface — the
/// free tier's entire visible output. The last line is where every prompt
/// here puts the turn itself, and what follows the turn on that line is
/// standing context every turn carries: "[Impulse: …] [Identity: …]
/// [Recall: …]". Echoing those back buried the one thing a reader was
/// looking for — what was actually said — under the same three brackets
/// every time.
/// </summary>
public sealed partial class MockSubstrateProvider : ISubstrateProvider
{
    [GeneratedRegex(@"^\s*0\.\s", RegexOptions.Multiline)]
    private static partial Regex EnumeratedCandidates { get; }

    /// <summary>
    /// The first " [Label: " that opens the run of asides. Matched on shape
    /// rather than on the known labels, so adding one to a prompt does not
    /// need a change here.
    /// </summary>
    [GeneratedRegex(@"\s\[[A-Z][^\[\]:]*:\s")]
    private static partial Regex FirstAside { get; }

    public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var text = EnumeratedCandidates.IsMatch(prompt) ? "0" : $"[mock:{substrateClass}] {Turn(prompt)}";
        return Task.FromResult(new SubstrateResult(text, TimeSpan.FromMilliseconds(5), prompt.Length / 4, 0m));
    }

    private static string Turn(string prompt)
    {
        var line = LastLine(prompt);
        var aside = FirstAside.Match(line);
        return aside.Success ? line[..aside.Index] : line;
    }

    private static string LastLine(string prompt)
    {
        var lines = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? prompt.Trim() : lines[^1];
    }
}
