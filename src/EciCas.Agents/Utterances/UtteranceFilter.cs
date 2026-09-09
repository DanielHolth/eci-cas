namespace EciCas.Agents.Utterances;

/// <summary>
/// Whether a sentence is worth keeping, decided without a model.
///
/// The inversion stores every utterance, and the objection to that is not
/// bytes -- it is that "haha ok" and "yeah" and "hmm" are vectors like any
/// other, and near-duplicate junk crowds a top-five read. The roadmap arm
/// asks whether the write side is filterable without a model. This is the
/// half that plainly is: a sentence with no content word in it carries no
/// claim, so there is nothing for a later read to recall from it.
///
/// **Content words, not characters.** A length cut in characters throws away
/// "Rex is 4" and keeps "well i mean i guess maybe sort of". The stopword
/// list already separates the two, and it was written for the lexical lane,
/// so this costs one set that was computed anyway.
///
/// **The turn still counts.** Only the row is dropped. Hit rates are per
/// turn, and a turn that said nothing storable is still a turn the archive
/// answered -- see ScribeAgent for why the counter advances first.
///
/// The novelty half of the arm -- dropping a restatement because the archive
/// already holds it -- is deliberately not here. Threading already collapses
/// restatements into one thread that reads back as its newest phrasing, so
/// dropping the row as well would buy nothing at read time and would lose
/// the one thing the log exists to hold: that it was said again, and when.
/// </summary>
public static class UtteranceFilter
{
    /// <summary>
    /// True if the utterance earns a row. Keywords are the content set from
    /// <see cref="KeywordExtractor.Content"/>, passed in rather than
    /// recomputed because the caller has already built it for the row.
    /// </summary>
    public static bool Keep(IReadOnlyCollection<string> keywords, UtteranceOptions options) =>
        keywords.Count >= options.MinContentWords;
}
