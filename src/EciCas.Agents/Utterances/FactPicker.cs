using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// The fan-in half of a fanout read: cosine casts wide, a model keeps what
/// the message is actually about.
///
/// **Why vectors cannot finish the job.** Short facts and short questions
/// land in a narrow band of cosine however unrelated they are, so the
/// shortlist is right about what is *near* and wrong about what *helps*.
/// "Who has birthday first, Marcus, Maia or Susana?" needs the nickname row
/// that says Maia is Maria Benita, and nothing in its vector says so.
///
/// **Numbers in, numbers out.** The model never rewrites a fact, so it
/// cannot invent one; the worst it can do is choose badly, and the rows it
/// chooses are rows the archive already holds.
///
/// **Null is "could not ask", empty is "asked, nothing helps".** The caller
/// falls back to cosine top-k on the first and hands on nothing on the
/// second -- a greeting should not arrive dressed in five random facts.
/// </summary>
public sealed partial class FactPicker
{
    /// <summary>The name its Substrates:Agents entry goes under.</summary>
    public const string AgentName = "picker";

    /// <summary>The reply that means "none of these help".</summary>
    public const string NothingRelevant = "NONE";

    private readonly ISubstrateProvider _substrate;
    private readonly ILogger<FactPicker> _logger;

    public FactPicker(ISubstrateProvider substrate, ILogger<FactPicker> logger)
    {
        _substrate = substrate;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Consulted>?> PickAsync(
        string message, IReadOnlyList<Consulted> shortlist, int max, CancellationToken cancellationToken)
    {
        if (shortlist.Count == 0)
        {
            return [];
        }

        try
        {
            var result = await _substrate.CompleteAsync(AgentName, BuildPrompt(message, shortlist, max), cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug("Picker <<< {Response}", result.Text);
            return Parse(result.Text, shortlist, max);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Picker failed; falling back to cosine order.");
            return null;
        }
    }

    private static string BuildPrompt(string message, IReadOnlyList<Consulted> shortlist, int max)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Pick the remembered facts that help reply to a message.");
        prompt.AppendLine();
        prompt.AppendLine("MESSAGE:");
        prompt.AppendLine(message);
        prompt.AppendLine();
        prompt.AppendLine("FACTS:");
        for (var i = 0; i < shortlist.Count; i++)
        {
            prompt.Append(i + 1).Append(". ").AppendLine(shortlist[i].Row.Text);
        }

        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine($"- Reply with the numbers of the facts that help, most useful first, comma-separated. At most {max}.");
        prompt.AppendLine("- A fact helps if it answers the message or is about a person, thing or topic the message names, nicknames included.");
        prompt.AppendLine("- When the message asks for a list, pick every fact that belongs in the list.");
        prompt.AppendLine("- Two facts saying the same thing: pick one.");
        prompt.AppendLine($"- If none help, reply {NothingRelevant}.");
        prompt.AppendLine();
        prompt.AppendLine("Numbers:");
        return prompt.ToString();
    }

    private static IReadOnlyList<Consulted>? Parse(string text, IReadOnlyList<Consulted> shortlist, int max)
    {
        if (text.Contains(NothingRelevant, StringComparison.Ordinal))
        {
            return [];
        }

        var picked = Numbers().Matches(text)
            .Select(m => int.Parse(m.Value) - 1)
            .Where(i => i >= 0 && i < shortlist.Count)
            .Distinct()
            .Take(max)
            .Select(i => shortlist[i])
            .ToList();

        // Prose with no usable number is a reply that answered something
        // else. That is a failure to ask, not a verdict of "nothing".
        return picked.Count > 0 ? picked : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
