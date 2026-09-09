using System.Text.RegularExpressions;

namespace EciCas.Agents.Utterances;

/// <summary>
/// The lexical half, with no model in it.
///
/// Embeddings are weak exactly where a query is a token -- names, dates,
/// numbers, rare words. "What is Rex's vet called" is a lexical question
/// wearing a semantic costume. This covers that blind spot, fused with
/// cosine rather than replacing it, and it is deterministic on purpose: a
/// model here would reintroduce the write-side call the inversion deleted.
///
/// **Two different sets, and conflating them is the mistake to avoid.**
///
/// <see cref="Content"/> is every non-stopword, lowercased. It depends on
/// the sentence and nothing else, which is why it is what gets written to
/// disk as ground truth: a set computed from a corpus is a set that changes
/// when the corpus does, and a hundred-year format cannot have a column
/// whose meaning drifts. It is also the right set for the consolidator's
/// shortcut, which asks whether two utterances *say the same thing* -- a
/// disagreement test over content words, not over names.
///
/// <see cref="Rare"/> is the filter batch 22 benched -- casing, digits, and
/// document frequency -- and it is applied at read time against the corpus
/// as it stands today. Rarity is a property of a corpus, so it is computed
/// where the corpus is, and recomputing it costs nothing because the content
/// set it filters was kept.
/// </summary>
public static partial class KeywordExtractor
{
    /// <summary>
    /// Closed-class English plus the conversational filler a spoken archive
    /// is made of. Hand-written rather than pulled from a package: the
    /// dependency is not worth it, and a list nobody can read is a list
    /// nobody can correct.
    /// </summary>
    private static readonly HashSet<string> Stop = new(
        """
        a an the this that these those there here it its
        i me my mine myself we us our ours you your yours he him his she her hers
        they them their theirs who whom whose which what where when why how
        is am are was were be been being do does did doing done have has had having
        will would shall should can could may might must
        and or but if then else so because as than of at by for with about against
        between into through during before after above below to from up down in out
        on off over under again further once not no nor only own same too very just
        s t now
        one two get got go going went make made take took come came say said
        thing things something anything nothing lot lots bit
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    [GeneratedRegex(@"[A-Za-z0-9'À-ſ]+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// Every non-stopword in the sentence, lowercased, in order, deduplicated.
    /// Ground truth: a pure function of the text, stable for as long as the
    /// text is.
    /// </summary>
    public static IReadOnlyList<string> Content(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keywords = new List<string>();
        foreach (Match match in WordPattern().Matches(text))
        {
            var word = Normalise(match.Value);
            if (word.Length > 0 && !Stop.Contains(word) && seen.Add(word))
            {
                keywords.Add(word);
            }
        }

        return keywords;
    }

    /// <summary>
    /// The benched filter, over a set <see cref="Content"/> already produced.
    ///
    /// Batch 22 settled the ordering and it is not stylistic: casing and
    /// digits alone keep a third of what an answer needs -- <em>penicillin</em>,
    /// <em>seasick</em>, <em>choir</em> are neither capitalised nor numeric,
    /// and they are most of what an archive is about -- so rarity carries the
    /// extractor and casing recovers the names stated often enough to stop
    /// being rare. Together they lose nothing against keeping everything.
    /// </summary>
    /// <param name="text">
    /// The original sentence, needed because casing does not survive
    /// <see cref="Content"/> and a capital is only evidence away from the
    /// first word -- every sentence starts with one.
    /// </param>
    public static IReadOnlyList<string> Rare(string text, IReadOnlyDictionary<string, int> documentFrequency, int rareMax)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordPattern().Matches(text))
        {
            var word = Normalise(match.Value);
            if (word.Length == 0 || Stop.Contains(word))
            {
                continue;
            }

            var capitalised = char.IsUpper(match.Value[0]) && match.Index > 0;
            var numeric = match.Value.Any(char.IsDigit);
            var rare = documentFrequency.GetValueOrDefault(word) <= rareMax;
            if (capitalised || numeric || rare)
            {
                admitted.Add(word);
            }
        }

        return [.. admitted];
    }

    /// <summary>
    /// How many utterances each word appears in. One pass over the corpus,
    /// counting a word once per row -- a word said six times in one sentence
    /// is not thereby common.
    /// </summary>
    public static Dictionary<string, int> DocumentFrequency(IEnumerable<IReadOnlyList<string>> keywordSets)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var set in keywordSets)
        {
            foreach (var word in set.Distinct(StringComparer.Ordinal))
            {
                df[word] = df.GetValueOrDefault(word) + 1;
            }
        }

        return df;
    }

    private static string Normalise(string word) => word.ToLowerInvariant().Trim('\'');
}
