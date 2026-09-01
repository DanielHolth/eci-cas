using System.Text.RegularExpressions;
using EciCas.Core;

namespace EciCas.Substrates;

/// <summary>
/// Zero-cost, zero-dependency substrate for the "mock" budget tier — every
/// substrate class resolves here until a manifest entry opts into "live".
///
/// Echoing the prompt back is enough for the agents that just need *a*
/// string, but not for the two that ask a numbered question: Reasoning
/// picking archive pairs and Recall picking rows both enumerate candidates
/// as "0. …", "1. …" and expect bare index numbers back. An echo parses to
/// nothing there, so the whole knowledge swarm silently took its empty path
/// on the free tier and could never be exercised without spending money.
/// The mock answers those with the first index instead. It keys off the
/// enumerated-candidate *format* the two prompts share, not their wording,
/// so rephrasing either prompt doesn't break it.
///
/// Everything else echoes the prompt's LAST line rather than the whole
/// thing. A whole prompt is a wall of rules and recalled context, and it
/// lands in a speech bubble on the companion surface — the free tier's
/// entire visible output. The last line is where every prompt here puts the
/// turn itself, so the echo stays diagnostic and becomes legible at the
/// same time.
/// </summary>
public sealed partial class MockSubstrateProvider : ISubstrateProvider
{
    [GeneratedRegex(@"^\s*0\.\s", RegexOptions.Multiline)]
    private static partial Regex EnumeratedCandidates { get; }

    public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var text = EnumeratedCandidates.IsMatch(prompt) ? "0" : $"[mock:{substrateClass}] {LastLine(prompt)}";
        return Task.FromResult(new SubstrateResult(text, TimeSpan.FromMilliseconds(5), prompt.Length / 4, 0m));
    }

    private static string LastLine(string prompt)
    {
        var lines = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? prompt.Trim() : lines[^1];
    }
}
