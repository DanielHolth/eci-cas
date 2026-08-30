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
/// </summary>
public static class PromptCap
{
    public const int DefaultMaxChars = 240;

    public static string Apply(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        return text[..maxChars] + "…";
    }
}
