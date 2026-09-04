namespace EciCas.Core;

/// <summary>
/// The standing text a substrate-calling agent sends on every turn, held
/// outside the assembly that uses it.
///
/// Instructions live in files rather than in C# because revising them is a
/// writing job, not a programming one: a rule that has to be recompiled to
/// change is a rule nobody trims. One file per agent, and one block per
/// agent inside it — two agents may want nearly the same sentence, and each
/// keeps its own copy, because a shared fragment cannot be revised for one
/// of them without silently revising the other.
///
/// Assembly stays in C#. A file holds the constant half of a prompt with
/// <c>{placeholder}</c> gaps where this turn's data goes, so what a person
/// edits is the whole of what the model reads.
/// </summary>
public interface IInstructionStore
{
    /// <summary>
    /// The named section of an agent's instruction file. "main" is
    /// everything before the first <c>## section</c> marker.
    /// </summary>
    string For(string agent, string section = InstructionFile.MainSection);
}

/// <summary>
/// Parsing and filling for one instruction file. Kept deliberately small:
/// section markers and <c>{placeholder}</c> substitution, no conditionals,
/// no loops. Anything a template language would be needed for belongs in
/// the agent, where it can be read as code.
/// </summary>
public static class InstructionFile
{
    public const string MainSection = "main";

    private const string SectionMarker = "## ";

    private const string CommentMarker = "#";

    /// <summary>
    /// Splits on lines beginning "## ". Text before the first marker is the
    /// main section, so a file that needs only one block is just prose with
    /// no markers in it at all.
    ///
    /// Lines beginning "# " are commentary and are stripped. Most of these
    /// files are prompts, where a note to the reader costs only tokens; but
    /// Identity, Impulse and Governance are read aloud, and there a file had
    /// no way to say anything the person was not going to hear — a fresh
    /// persona introduced itself by explaining what an identity file is for.
    /// A convention the parser enforces is the only kind that survives the
    /// next person who documents a file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = MainSection;
        var body = new List<string>();

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
            {
                sections[name] = string.Join('\n', body).Trim();
                name = line[SectionMarker.Length..].Trim();
                body.Clear();
                continue;
            }

            // Anything else starting '#' is commentary. Checked after the
            // marker so "## section" stays a marker, and by first character
            // rather than "# " so a bare '#' spacer line counts too.
            if (line.StartsWith(CommentMarker, StringComparison.Ordinal))
            {
                continue;
            }

            body.Add(line);
        }

        sections[name] = string.Join('\n', body).Trim();
        return sections;
    }

    /// <summary>Every <c>{placeholder}</c> named in the text, in no particular order.</summary>
    public static IReadOnlySet<string> PlaceholdersIn(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
        {
            var close = text.IndexOf('}', i + 1);
            if (close < 0)
            {
                break;
            }

            var name = text[(i + 1)..close];
            if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Substitutes this turn's values. Unknown placeholders are left alone
    /// rather than blanked — a gap the agent did not fill is visible in the
    /// prompt, where an empty string would look like the model was simply
    /// told nothing.
    /// </summary>
    public static string Fill(string text, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }
}
