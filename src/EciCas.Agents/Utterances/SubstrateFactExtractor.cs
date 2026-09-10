using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// Reads the facts out of what somebody said, as sentences that stand on
/// their own.
///
/// **Why there is a model call here at all.** The inversion deleted the
/// write-side call and was right to: what lost that bench was the closed
/// vocabulary -- two calls to guess a category and a key, and a wrong guess
/// unreachable forever. This is not that. It produces free text, once per
/// turn, into a store that is derived; a bad extraction is a recomputation,
/// not a legacy.
///
/// **Why a regex could not do it.** "We moved to Bodø in 2019. It's colder
/// than Ingrid expected." splits on punctuation into a row that reads "It's
/// colder than Ingrid expected", which answers no question anybody will ever
/// ask, because the thing it is about is on the other row. Splitting is
/// syntax; what retrieval needs is the antecedents put back, and that is
/// language.
///
/// **One call for the whole utterance, never one per fact.** The sentences
/// resolve each other's pronouns. A per-fact call would have thrown away the
/// context that made them resolvable, which is the entire job.
///
/// **Every utterance is asked, however short.** A length gate once let
/// short turns through verbatim, and short turns are mostly questions:
/// "how many kids do i have?" became a fact and answered the next read.
///
/// **It never throws for content reasons.** Substrate down, deadline blown,
/// reply unparseable -- the answer is the same, the utterance verbatim as one
/// fact. An unsplit row retrieves badly; an absent row does not retrieve at
/// all. Ground truth is already on disk either way, so a turn can lose its
/// index and get it back from the backfill, and can never lose what was said.
/// </summary>
public sealed class SubstrateFactExtractor : IFactExtractor
{
    /// <summary>The name its Substrates:Agents entry goes under.</summary>
    public const string AgentName = "extractor";

    /// <summary>The reply that means "said nothing to keep".</summary>
    public const string NothingStated = "NONE";

    private readonly ISubstrateProvider _substrate;
    private readonly UtteranceOptions _options;
    private readonly ILogger<SubstrateFactExtractor> _logger;

    public SubstrateFactExtractor(ISubstrateProvider substrate, IOptions<UtteranceOptions> options,
        ILogger<SubstrateFactExtractor> logger)
    {
        _substrate = substrate;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken)
    {
        var text = utterance.Text.Trim();
        if (!_options.ExtractorEnabled)
        {
            return [text];
        }

        try
        {
            var result = await _substrate.CompleteAsync(AgentName, BuildPrompt(text, previousReply), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Extractor <<< {Response}", result.Text);

            // NONE is the model saying "nothing was claimed": a question, a
            // greeting. Stored as a fact, a question comes back later as the
            // answer to itself. An empty or garbled reply is a failure, not a
            // verdict, and keeps the utterance whole like any other failure.
            if (result.Text.Trim().Trim('.').Equals(NothingStated, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var facts = Parse(result.Text, _options.ExtractorMaxFacts);
            return facts.Count > 0 ? facts : [text];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Loud: a substrate down for a week is a week of an archive that
            // holds paragraphs where it should hold facts. Recoverable, but
            // only by somebody who knows to run the backfill.
            _logger.LogWarning(ex, "Extractor failed; keeping the utterance whole.");
            return [text];
        }
    }

    /// <summary>
    /// The prompt asks for decontextualisation and forbids invention, in that
    /// order, because those are the two failure modes and only one of them is
    /// recoverable. A fact left joined to its neighbour retrieves badly; a
    /// fact the person never stated is the archive lying to itself, and the
    /// consolidator downstream will happily thread it.
    /// </summary>
    internal static string BuildPrompt(string text, string? previousReply)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Rewrite what somebody said as a list of standalone facts.");
        prompt.AppendLine();
        if (!string.IsNullOrWhiteSpace(previousReply))
        {
            prompt.AppendLine("WHAT WAS SAID TO THEM JUST BEFORE (context for references only; take no facts from it):");
            prompt.AppendLine(PromptCap.Apply(previousReply.Trim(), 1500));
            prompt.AppendLine();
        }

        prompt.AppendLine("WHAT THEY SAID:");
        prompt.AppendLine(text);
        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- One fact per line. No numbering, no bullets, no commentary.");
        prompt.AppendLine("- Each line must make sense alone, read years later, by someone who cannot see the other lines. Replace every pronoun and every \"there\", \"then\", \"that one\" with the thing it refers to.");
        prompt.AppendLine("- Keep the speaker's own words and their names for things wherever you can. You are putting the missing pieces back, not rephrasing.");
        prompt.AppendLine("- Keep first person as first person: \"I moved to Bodo in 2019\", not \"the speaker moved to Bodo in 2019\".");
        prompt.AppendLine("- Add nothing that was not said. If you are unsure whether something was claimed, leave it out.");
        prompt.AppendLine("- Questions, greetings, requests and small talk state nothing. Skip them. A question can still contain a fact (\"now that Rex is 4, should he be neutered?\" states that Rex is 4).");
        prompt.AppendLine("- Reactions state nothing on their own: \"I totally agree\", \"exactly\", \"thanks\", \"cool\". Skip them. Only when they say what they agree with, or the text before makes it unmistakable, write that as the fact (\"I agree that X\").");
        prompt.AppendLine("- Drop any line that still depends on something outside itself (\"the first knob\", \"that idea\"). If you cannot say what it refers to, it is not a fact.");
        prompt.AppendLine($"- If nothing at all is stated, reply with the single word {NothingStated}.");
        prompt.AppendLine("- If the whole thing is already one standalone fact, repeat it back unchanged as the only line.");
        prompt.AppendLine();
        prompt.AppendLine("Facts:");
        return prompt.ToString();
    }

    /// <summary>
    /// Lines, stripped of the bullets and numbering the prompt asked it not
    /// to use. Asking twice is cheaper than a corpus where a tenth of the
    /// rows begin "1. ".
    /// </summary>
    private static IReadOnlyList<string> Parse(string text, int max)
    {
        var facts = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', ' ');

            // A leading "12." is numbering; a leading "2019" is a fact.
            var digits = line.TakeWhile(char.IsDigit).Count();
            if (digits > 0 && digits < line.Length && (line[digits] == '.' || line[digits] == ')'))
            {
                line = line[(digits + 1)..].Trim();
            }

            if (line.Length > 1 && !line.EndsWith(':'))
            {
                facts.Add(line);
            }

            if (facts.Count == max)
            {
                break;
            }
        }

        return facts;
    }
}

/// <summary>
/// No extractor. Every utterance is its own single fact, verbatim -- which
/// is exactly the behaviour of the archive before the split, and therefore
/// the honest floor for a tier with no call to spend.
/// </summary>
public sealed class VerbatimFactExtractor : IFactExtractor
{
    public Task<IReadOnlyList<string>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([utterance.Text.Trim()]);
}
