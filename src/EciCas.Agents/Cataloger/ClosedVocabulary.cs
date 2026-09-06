using EciCas.Core;

namespace EciCas.Agents.Cataloger;

/// <summary>
/// The list of addresses the archive is allowed to have, parsed once from
/// cataloger.txt's "vocabulary" section.
///
/// It lives in the instruction file rather than in C# or appsettings because
/// it is read by both halves of the job: the same words are shown to the
/// model in the topic prompt and used here to check what came back. Split
/// across two files they would drift, and the drift would be invisible — a
/// topic offered but not accepted just becomes a dropped fact.
///
/// Closed on purpose, and the reason is the store: ParquetArchiveStore names
/// one file per category/topic pair and there is no index beside it, so an
/// invented name is a new file nobody looks in, and a correction stated
/// months later lands somewhere other than the fact it corrects.
/// </summary>
public sealed class ClosedVocabulary
{
    public const string OtherTopic = "other";

    private readonly Dictionary<string, string[]> _topics;

    private ClosedVocabulary(Dictionary<string, string[]> topics) => _topics = topics;

    public IReadOnlyCollection<string> Categories => _topics.Keys;

    public IReadOnlyList<string> TopicsIn(string category) => _topics[category];

    /// <summary>
    /// "category: topic topic ..." with continuation lines — a line that
    /// starts indented belongs to the category above it. Written that way so
    /// twenty topics can be read at a glance without a horizontal scroll.
    /// </summary>
    public static ClosedVocabulary Parse(string section)
    {
        var topics = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var current = string.Empty;

        foreach (var raw in section.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.Trim().Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(raw[0]) && raw.Contains(':'))
            {
                var split = raw.Split(':', 2);
                current = split[0].Trim().ToLowerInvariant();
                topics[current] = Words(split[1]);
                continue;
            }

            if (current.Length > 0)
            {
                topics[current] = [.. topics[current], .. Words(raw)];
            }
        }

        if (topics.Count == 0)
        {
            throw new InvalidOperationException("cataloger.txt's '## vocabulary' section declares no categories.");
        }

        foreach (var (category, list) in topics)
        {
            if (!list.Contains(OtherTopic, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"category '{category}' has no '{OtherTopic}' topic — every category needs one, it is where a fact goes when no folder fits.");
            }
        }

        return new ClosedVocabulary(topics);
    }

    private static string[] Words(string line) =>
        [.. line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())];

    /// <summary>
    /// The pair a fact ends up at, or null if the model named nothing on the
    /// list. Matched by containment rather than equality: a 4B answers
    /// "identity" about as often as it answers "The drawer is identity."
    /// </summary>
    public string? MatchCategory(string reply)
    {
        var lowered = reply.ToLowerInvariant();
        return _topics.Keys.FirstOrDefault(c => lowered.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whole words only, unlike the category match: topic lists contain
    /// "name" and "nationality", and a containment test would read the first
    /// out of the second. An unlisted answer becomes "other" rather than a
    /// dropped fact — the category was already decided, and a fact in the
    /// right drawer with the wrong folder is still reachable.
    /// </summary>
    public string MatchTopic(string category, string reply)
    {
        var listed = _topics[category];
        var words = reply.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', '"', '\'', '*', '-', ':'));

        return words.FirstOrDefault(listed.Contains) ?? OtherTopic;
    }
}
