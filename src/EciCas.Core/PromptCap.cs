using System.Text.RegularExpressions;

namespace EciCas.Core;

/// <summary>
/// Bounds a single piece of upstream text before it's folded into a prompt.
/// Every CognitiveAgent&lt;T&gt; hop re-embeds what the previous hop said
/// (Intent's reply becomes Reflection's input; Reflection's idea becomes
/// the next turn's perceived text), so a genuinely unbounded string one hop
/// upstream would otherwise compound across every future hop it passes
/// through. Capping what each hop contributes — not tracking or trimming
/// history — is enough to make that impossible: the ceiling per hop stays
/// fixed no matter how many generations deep the loop runs.
///
/// It also flattens. Every call site folds the result into a slot that is
/// one line by construction — a bracketed aside like "[Identity: …]", or an
/// entry in a numbered list the model is asked to answer by index. A value
/// carrying its own newlines splits that slot in half and the structure the
/// prompt was counting on is gone.
/// </summary>
public static partial class PromptCap
{
    public const int DefaultMaxChars = 240;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun { get; }

    public static string Apply(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Flatten before measuring, so the cap counts what the prompt will
        // actually carry rather than the indentation of the source file it
        // was written in.
        var flat = WhitespaceRun.Replace(text, " ").Trim();

        return flat.Length <= maxChars ? flat : flat[..maxChars] + "…";
    }
}
