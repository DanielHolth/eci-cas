using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// The consolidator, backed by a substrate call.
///
/// Not a <c>CognitiveAgent&lt;T&gt;</c>, and deliberately: cognitive agents
/// are bus handlers, one call per turn, inside the bundle's deadline. This
/// runs after the turn is answered, once per unresolved candidate set, zero
/// times on most turns and twice on some. It borrows the substrate registry
/// -- so a tier can point it at a cheap model, or at none -- and nothing
/// else.
///
/// **The prompt asks one question and permits refusal.** Choosing wrongly
/// among five is worse than choosing none, because a wrong merge is the
/// unrecoverable error and "none" only costs a duplicate. So 0 is offered
/// first and described as the safe answer rather than as failure.
/// </summary>
public sealed class SubstrateConsolidator : IUtteranceConsolidator
{
    /// <summary>The name its Substrates:Agents entry goes under.</summary>
    public const string AgentName = "consolidator";

    private readonly ISubstrateProvider _substrate;
    private readonly ILogger<SubstrateConsolidator> _logger;

    public SubstrateConsolidator(ISubstrateProvider substrate, ILogger<SubstrateConsolidator> logger)
    {
        _substrate = substrate;
        _logger = logger;
    }

    public async Task<ConsolidatorVerdict?> AdjudicateAsync(Utterance incoming, IReadOnlyList<Utterance> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var result = await _substrate.CompleteAsync(AgentName, BuildPrompt(incoming, candidates), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Consolidator <<< {Response}", result.Text);
        return Parse(result.Text, candidates);
    }

    private static string BuildPrompt(Utterance incoming, IReadOnlyList<Utterance> candidates)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are sorting statements about one person into running subjects.");
        prompt.AppendLine();
        prompt.AppendLine("NEW STATEMENT:");
        prompt.AppendLine(incoming.Text);
        prompt.AppendLine();
        prompt.AppendLine("EXISTING SUBJECTS:");
        for (var i = 0; i < candidates.Count; i++)
        {
            prompt.Append(CultureInfo.InvariantCulture, $"{i + 1}. {candidates[i].Text}");
            prompt.AppendLine();
        }

        prompt.AppendLine();
        prompt.AppendLine("Does the new statement belong to one of these subjects?");
        prompt.AppendLine("Two statements share a subject when they are about the same thing in the same person's life -- the same car, the same job, the same relationship -- even if they say different things about it.");
        prompt.AppendLine("They do NOT share a subject when they merely sound alike, or are about different people, or about different instances of a similar thing.");
        prompt.AppendLine();
        prompt.AppendLine("Answer with a number and nothing else:");
        prompt.AppendLine("  0  -- none of them. This is always a safe answer; prefer it whenever you are unsure.");
        prompt.AppendLine("  N  -- subject N, and both statements are still true.");
        prompt.AppendLine("  N* -- subject N, and the new statement replaces it (it is no longer true).");
        return prompt.ToString();
    }

    /// <summary>
    /// One number, or nothing. An unparseable answer is a refusal, not a
    /// retry: the cost of being wrong here is permanent and the cost of a
    /// duplicate is a read-time collapse that already exists.
    /// </summary>
    private ConsolidatorVerdict? Parse(string text, IReadOnlyList<Utterance> candidates)
    {
        var trimmed = text.Trim();
        var supersedes = trimmed.Contains('*', StringComparison.Ordinal);
        var digits = new string([.. trimmed.TakeWhile(char.IsDigit)]);

        if (!int.TryParse(digits, CultureInfo.InvariantCulture, out var pick) || pick < 0 || pick > candidates.Count)
        {
            _logger.LogWarning("Consolidator returned no usable choice ({Text}); treating as none.", trimmed);
            return null;
        }

        if (pick == 0)
        {
            return null;
        }

        var chosen = candidates[pick - 1];
        return new ConsolidatorVerdict(chosen.ThreadId, supersedes ? chosen.Id : null);
    }
}

/// <summary>
/// No consolidator. Every candidate set resolves to "none", which is the
/// split the deterministic tier is designed around -- see
/// <see cref="UtteranceOptions.ConsolidatorEnabled"/> for why splitting is
/// the safe direction.
/// </summary>
public sealed class NullUtteranceConsolidator : IUtteranceConsolidator
{
    public Task<ConsolidatorVerdict?> AdjudicateAsync(Utterance incoming, IReadOnlyList<Utterance> candidates, CancellationToken cancellationToken) =>
        Task.FromResult<ConsolidatorVerdict?>(null);
}
