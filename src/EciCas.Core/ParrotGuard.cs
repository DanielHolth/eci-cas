namespace EciCas.Core;

/// <summary>
/// Ported verbatim (algorithm, not text) from the Python prototype's
/// agents/intent/contract.py is_parroting(): true when speech is its source
/// "with the serial numbers filed off" — an exact echo, or the source
/// verbatim inside a wrapper of 4 words or fewer. Catches a live node with
/// nothing to say that quietly re-breaks the boundary between analysis
/// (Reasoning writes it) and speech (Intent voices it).
/// </summary>
public static class ParrotGuard
{
    public static bool IsParroting(string speech, string? source)
    {
        var a = string.Join(' ', speech.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var b = string.Join(' ', (source ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (b.Length == 0)
        {
            return false;
        }

        if (a == b)
        {
            return true;
        }

        if (b.Length <= 12 || !a.Contains(b, StringComparison.Ordinal))
        {
            return false;
        }

        var wrapperWordCount = a.Replace(b, " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Trim(PunctuationChars).Length > 0);

        return wrapperWordCount <= 4;
    }

    private static readonly char[] PunctuationChars = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".ToCharArray();
}
