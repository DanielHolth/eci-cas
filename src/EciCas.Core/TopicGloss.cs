namespace EciCas.Core;

/// <summary>
/// A few words of plain synonym for each category/topic pair, parsed from
/// cataloger.txt's "gloss" section.
///
/// It lives beside the vocabulary it describes, for the reason that section's
/// own comment gives: a name and its definition split across two files drift,
/// and the drift is invisible. It is read by both sides — Librarian shows it
/// to the selector, and the intent is that Cataloger shows the same words to
/// the filer — so that the writer and the reader are working from one
/// dictionary rather than two. A folder means what the gloss says it means.
///
/// The words are deliberately in the *question's* vocabulary, not the
/// folder's: "renewal" is glossed "expires, expiry, renew, runs out, valid
/// until" because a person asks when something runs out and never asks about
/// their renewals. That is the whole mechanism — the selector is matching a
/// turn against option lines, and a folder name a question would never use
/// cannot be matched however apt it is.
///
/// Measured before shipping, twice, on the 170-pair shelf with filing held
/// byte-identical: select 41% -> 53%, answer 30% -> 43%, and the small-talk
/// false-positive rate improved as well, 71% -> 84% quiet. Seven reps across
/// two independent runs with no negative rep on answer.
///
/// "other" has no gloss and needs none: it is never shown to a selector.
/// </summary>
public sealed class TopicGloss
{
    private readonly Dictionary<string, string> _words;

    private TopicGloss(Dictionary<string, string> words) => _words = words;

    /// <summary>"category/topic: words, words, words", one per line.</summary>
    public static TopicGloss Parse(string section)
    {
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in section.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.Split(':', 2);
            if (split.Length == 2 && split[0].Contains('/') && split[1].Trim().Length > 0)
            {
                words[split[0].Trim()] = split[1].Trim();
            }
        }

        return new TopicGloss(words);
    }

    /// <summary>
    /// The gloss for a pair, or null when it has none. Null rather than throw:
    /// the archive can hold a pair the gloss has not caught up with, and a
    /// missing definition should cost that one option line its extra words,
    /// not cost the turn its whole selection.
    /// </summary>
    public string? For(string category, string topic) =>
        _words.TryGetValue(category + "/" + topic, out var w) ? w : null;

    public int Count => _words.Count;
}
