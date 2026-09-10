using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Given a new fact and the handful of older facts nearest it, decides which
/// running subject it belongs to and whether it makes one of them false.
///
/// **Its job changed when the archive split.** It used to be handed raw
/// utterances -- whole turns, several claims each, half of them pronouns --
/// and asked whether two of those were "the same subject", which is a
/// question about paragraphs that often has no answer. It is now handed
/// facts: one claim per row, every antecedent already resolved. That is a
/// question about two sentences, and it is the question this size of model
/// can actually answer.
///
/// **Two questions, one call.** Same subject, and still both true. They come
/// together because the second only means anything once the first is settled:
/// "I drive a Tesla" contradicts "I drive a Subaru" and merely differs from
/// "Ingrid drives a Subaru", and nothing but the subject tells them apart.
///
/// **Refusal is offered first and named as safe.** The errors are not
/// symmetric. A false split is today's behaviour -- a duplicate, collapsed at
/// read time, recoverable by a later pass. A false merge glues two subjects
/// together, and a false supersession marks a true fact dead; both make *now*
/// wrong in a way no read can undo. So 0 leads the list, and anything the
/// parser cannot read is a 0.
///
/// Not a <c>CognitiveAgent&lt;T&gt;</c>, deliberately: cognitive agents are
/// bus handlers, one call per turn, inside the bundle's deadline. This runs
/// after the turn is answered, once per unresolved candidate set, zero times
/// on most turns and several times on some. It borrows the substrate registry
/// -- so a tier can point it at a cheap model, or at none -- and nothing else.
/// </summary>
public sealed class SubstrateConsolidator : IFactConsolidator
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

    public async Task<ConsolidatorVerdict?> AdjudicateAsync(Fact incoming, IReadOnlyList<Fact> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var result = await _substrate.CompleteAsync(AgentName, BuildPrompt(incoming, candidates), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Consolidator <<< {Response}", result.Text);
        return Parse(result.Text, candidates);
    }

    private static string BuildPrompt(Fact incoming, IReadOnlyList<Fact> candidates)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are filing one new fact about a person against the facts already on file.");
        prompt.AppendLine();
        prompt.AppendLine("NEW FACT:");
        prompt.AppendLine(incoming.Text);
        prompt.AppendLine();
        prompt.AppendLine("ON FILE:");
        for (var i = 0; i < candidates.Count; i++)
        {
            prompt.Append(CultureInfo.InvariantCulture, $"{i + 1}. {candidates[i].Text}");
            prompt.AppendLine();
        }

        prompt.AppendLine();
        prompt.AppendLine("Two facts are about the same subject when they concern the same thing in the same person's life -- the same car, the same job, the same animal, the same relationship -- even when they say different things about it.");
        prompt.AppendLine("They are NOT about the same subject when they merely sound alike, or belong to different people, or describe two different things of the same kind.");
        prompt.AppendLine();
        prompt.AppendLine("Answer with one token and nothing else:");
        prompt.AppendLine("  0  -- none of them is about the same subject. This is always safe; answer 0 whenever you are unsure.");
        prompt.AppendLine("  N  -- same subject as N, and both are still true.");
        prompt.AppendLine("  N* -- same subject as N, and the new fact replaces it: N cannot still be true.");
        return prompt.ToString();
    }

    /// <summary>
    /// One number, or nothing. An unparseable answer is a refusal rather than
    /// a retry -- see the class remarks on why the errors are not symmetric.
    /// </summary>
    private ConsolidatorVerdict? Parse(string text, IReadOnlyList<Fact> candidates)
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
public sealed class NullFactConsolidator : IFactConsolidator
{
    public Task<ConsolidatorVerdict?> AdjudicateAsync(Fact incoming, IReadOnlyList<Fact> candidates, CancellationToken cancellationToken) =>
        Task.FromResult<ConsolidatorVerdict?>(null);
}
