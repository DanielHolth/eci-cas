namespace EciCas.Core;

/// <summary>
/// The persona's own shelf, and the reason it is a SCOPE rather than a
/// category.
///
/// A bi-encoder has nowhere to encode whose fact a row is. Measured
/// directly (tools/retrieval-bench/refl_v4.py): routing a turn that asks
/// the persona for its own view by similarity puts 1 of 16 into an
/// assistant drawer, while 6 of 6 human controls correctly stay out. So
/// "assistant" cannot be a thirty-third category ranked against
/// `household` and hope to win — it has to be decided BEFORE ranking, by
/// who the question is about, and then only its own drawers compete.
///
/// That is already how the archive stores it: these categories are the
/// default of <c>Archive:SharedCategories</c>, so their rows are filed
/// per-device rather than per-profile and never mix with a person's. This
/// type exists so the scope is declared in one place instead of as a
/// string literal in each agent that writes into it — a fourth drawer
/// added by hand in one file and not the others is the failure it
/// prevents.
///
/// COARSE ON PURPOSE. The 512-pair vocabulary is the user's domain and
/// says so in its own header; nothing here is ranked against it. Three
/// drawers is not an oversight, it is the whole shelf: what the persona
/// is, what it has thought, and what it runs on. A fourth needs an
/// argument, because every one of these is a place a person's fact could
/// be misfiled into.
/// </summary>
public static class AssistantScope
{
    /// <summary>The scope name, which is also the archive category. Shared across profiles.</summary>
    public const string Name = "assistant";

    /// <summary>What the persona is: its description, as facts rather than as the snippet IdentityAgent serves.</summary>
    public const string Persona = "persona";

    /// <summary>What the persona has thought. Written by Reflection, woken by Hindsight, never by a lookup.</summary>
    public const string Reflection = "reflection";

    /// <summary>What the persona runs on. Version, build, the row that keeps a new archive from being truly empty.</summary>
    public const string System = "system";

    /// <summary>Every drawer on the shelf. Ordered as declared, and short enough to read.</summary>
    public static readonly string[] Topics = [Persona, Reflection, System];

    /// <summary>
    /// True when a category belongs to the persona rather than to a person.
    /// Ordinal on purpose: these are addresses, not prose.
    /// </summary>
    public static bool Owns(string? category) =>
        string.Equals(category, Name, StringComparison.Ordinal);
}
