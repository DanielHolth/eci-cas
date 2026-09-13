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
    /// <summary>The name the live path's Substrates:Agents entry goes under.</summary>
    public const string AgentName = "extractor";

    /// <summary>The reply that means "said nothing to keep".</summary>
    public const string NothingStated = "NONE";

    private readonly ISubstrateProvider _substrate;
    private readonly UtteranceOptions _options;
    private readonly ILogger<SubstrateFactExtractor> _logger;

    /// <summary>
    /// Which substrate entry this instance calls. An instance field rather
    /// than the constant, so the boot rebuild is a second instance of this
    /// class pointed at a stronger model -- not a second implementation.
    ///
    /// The rules are the same rules. What a fact is, how a pronoun resolves,
    /// what the four fields mean, which words are classes: all of that is
    /// one prompt in one place, and a rebuild that reasoned differently from
    /// the live write would be rebuilding into a schema nobody else holds.
    /// The only thing that differs between the two is who is asked.
    /// </summary>
    private readonly string _agent;

    public SubstrateFactExtractor(ISubstrateProvider substrate, IOptions<UtteranceOptions> options,
        ILogger<SubstrateFactExtractor> logger, string agent = AgentName)
    {
        _substrate = substrate;
        _options = options.Value;
        _logger = logger;
        _agent = agent;
    }

    public async Task<IReadOnlyList<ExtractedFact>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken)
    {
        var text = utterance.Text.Trim();
        if (!_options.ExtractorEnabled)
        {
            return [new ExtractedFact(text)];
        }

        try
        {
            // The day the utterance was said, never today: "yesterday"
            // points at the day before it was spoken, and a rebuild running
            // a year later against its own clock would resolve every
            // relative date in the archive to the day of the rebuild.
            var prompt = BuildPrompt(text, _options.ExtractorSeesPreviousReply ? previousReply : null, utterance.Timestamp);
            var result = await _substrate.CompleteAsync(_agent, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Extractor <<< {Response}", result.Text);

            // NONE is the model saying "nothing was claimed": a question, a
            // greeting. Stored as a fact, a question comes back later as the
            // answer to itself. An empty or garbled reply is a failure, not a
            // verdict, and keeps the utterance whole like any other failure.
            //
            // It comes back as one empty extraction rather than none, signed
            // with who said it. The caller turns that into a marker row, and
            // the marker is the whole point: without it the turn looks
            // unread at every boot and is paid for again every boot, for a
            // verdict that will never change. Signed, because a better model
            // is allowed to disagree with a weak one's NONE -- which is
            // exactly what the boot rebuild already does with weak facts.
            if (result.Text.Trim().Trim('.').Equals(NothingStated, StringComparison.OrdinalIgnoreCase))
            {
                return [new ExtractedFact(string.Empty, OriginModel: result.Model)];
            }

            var facts = Parse(result.Text, _options.ExtractorMaxFacts, result.Model);
            return facts.Count > 0 ? facts : [new ExtractedFact(text, OriginModel: result.Model)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Loud: a substrate down for a week is a week of an archive that
            // holds paragraphs where it should hold facts. Recoverable, but
            // only by somebody who knows to run the backfill.
            _logger.LogWarning(ex, "Extractor failed; keeping the utterance whole.");
            return [new ExtractedFact(text)];
        }
    }

    /// <summary>
    /// The prompt asks for decontextualisation and forbids invention, in that
    /// order, because those are the two failure modes and only one of them is
    /// recoverable. A fact left joined to its neighbour retrieves badly; a
    /// fact the person never stated is the archive lying to itself, and the
    /// consolidator downstream will happily thread it.
    /// </summary>
    internal static string BuildPrompt(string text, string? previousReply) =>
        BuildPrompt(text, previousReply, DateTimeOffset.UtcNow);

    internal static string BuildPrompt(string text, string? previousReply, DateTimeOffset saidAt)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Rewrite what somebody said as a list of standalone facts.");
        prompt.AppendLine();
        prompt.AppendLine($"TODAY IS: {saidAt:yyyy-MM-dd} ({saidAt:dddd}).");
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
        prompt.AppendLine("Write one fact per line, in four fields separated by \" | \":");
        prompt.AppendLine();
        prompt.AppendLine("  fact | class | entity | sensitivity");
        prompt.AppendLine();
        prompt.AppendLine($"class is exactly one of: {string.Join(", ", FactClasses.All)}");
        prompt.AppendLine("entity is what the fact is about -- a person, a place, a thing -- named the way the speaker names it. Use the speaker's own name for themselves if you know it, otherwise \"user\".");
        prompt.AppendLine("sensitivity is 0 for something they would say to a stranger, 1 for something personal they would say to a friend, 2 for something private -- health, money, somebody else's secrets, anything that must never appear on a shared screen.");
        prompt.AppendLine();
        prompt.AppendLine("Example:");
        prompt.AppendLine("  Ingrid's birthday is 1988-03-04 | date | Ingrid | 1");
        prompt.AppendLine("  I play bass | skill | user | 0");
        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- One fact per line. No numbering, no bullets, no commentary.");
        prompt.AppendLine("- Each line must make sense alone, read years later, by someone who cannot see the other lines. Replace every pronoun and every \"there\", \"then\", \"that one\" with the thing it refers to.");
        prompt.AppendLine("- Resolve time the same way you resolve pronouns. \"yesterday\", \"last night\", \"in two weeks\" are references, and TODAY IS above is what they point at. Write the date: \"Marcus had his birthday yesterday\" becomes \"Marcus had his birthday on 2026-09-12\". A fact that keeps a relative date is only true on the day it was said.");
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
    private static IReadOnlyList<ExtractedFact> Parse(string text, int max, string? model)
    {
        var facts = new List<ExtractedFact>();
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
                facts.Add(Split(line, model));
            }

            if (facts.Count == max)
            {
                break;
            }
        }

        return facts;
    }

    /// <summary>
    /// One line into its four fields.
    ///
    /// The text is the part that must survive, so every other field is
    /// optional and a malformed line degrades to "a fact nobody classified"
    /// rather than to nothing. A model that forgets the pipes still gets its
    /// sentence stored; the columns are derived and the next rebuild fills
    /// them in.
    ///
    /// A pipe inside the sentence would otherwise eat the fact, so the split
    /// counts from the right: the last three fields are the metadata and
    /// everything before them is what was said.
    /// </summary>
    private static ExtractedFact Split(string line, string? model)
    {
        var parts = line.Split('|');
        if (parts.Length < 4)
        {
            return new ExtractedFact(line.Trim(), OriginModel: model);
        }

        var sensitivity = int.TryParse(parts[^1].Trim(), out var level) ? Math.Clamp(level, 0, 2) : (int?)null;
        var entity = parts[^2].Trim();
        var written = parts[^3].Trim();
        var fact = string.Join('|', parts[..^3]).Trim();

        return fact.Length == 0
            ? new ExtractedFact(line.Trim(), OriginModel: model)
            : new ExtractedFact(
                fact,
                FactClasses.Normalise(written),
                entity.Length == 0 ? null : entity,
                sensitivity,
                model);
    }
}

/// <summary>
/// No extractor. Every utterance is its own single fact, verbatim -- which
/// is exactly the behaviour of the archive before the split, and therefore
/// the honest floor for a tier with no call to spend.
/// </summary>
public sealed class VerbatimFactExtractor : IFactExtractor
{
    // Every column null, and that is the honest record: no model looked at
    // this, so nobody has an opinion about what kind of thing it is. A null
    // origin also puts the row first in line for the next rebuild.
    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExtractedFact>>([new ExtractedFact(utterance.Text.Trim())]);
}
