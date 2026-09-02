namespace EciCas.Core;

/// <summary>
/// Knowledge-swarm archive: the semantic two-stage store (Librarian selects
/// Category/Topic pairs, Recall reads rows within a pair).
/// One record per fact, nine fields, all lowercase by convention.
///
/// Subtopic is still carried on every record, but it is no longer part of
/// the address: it is data the picking model reads, not a key anyone looks
/// up by. That keeps a deeply-discussed subtopic from needing its own index
/// entry, and lets Recall slice one pair across as many parallel workers as
/// its row count warrants.
///
/// Every member takes the profile whose turn this is — the opaque id
/// PerceptionAgent.ProfileKey carries — because personal knowledge is
/// scoped per person while world knowledge is shared. Null means no profile
/// (the console loop, Reflection's own ideas) and addresses the shared tier
/// alone, which is exactly the single-user behaviour that predates profiles.
/// </summary>
public interface IArchiveStore
{
    /// <summary>Distinct (Category, Topic) pairs visible to this profile — shared plus its own — for Librarian's selection prompt.</summary>
    IReadOnlyList<ArchivePair> IndexFor(string? profileId);

    /// <summary>
    /// Every row under this pair, in a stable Importance-descending order,
    /// unioned across the shared tier and this profile's own — the profile
    /// winning where both hold the same subtopic/subject/key.
    /// Deliberately uncapped: a subtopic discussed at great length must not
    /// be truncated away. Recall reads a pair exactly once and chunks the
    /// result across its workers in memory, so a deep pair costs one file
    /// read no matter how many substrate calls it fans out into.
    /// </summary>
    Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchivePair pair, string? profileId, CancellationToken cancellationToken);

    /// <summary>Writes to this profile's own tier, except for categories the store treats as shared.</summary>
    Task WriteAsync(IReadOnlyList<ArchiveRecord> records, string? profileId, CancellationToken cancellationToken);
}

public sealed record ArchivePair(string Category, string Topic);

public sealed record ArchiveRecord(
    string Category,
    string Topic,
    string Subtopic,
    string Subject,
    string Key,
    string Value,
    DateTimeOffset Timestamp,
    string Domain = ArchiveDomain.External,
    double Importance = 0.5)
{
    public ArchivePair Pair => new(Category, Topic);
}

public static class ArchiveDomain
{
    public const string External = "external";
    public const string Internal = "internal";
}

/// <summary>
/// Shared prompt language for how much text belongs in an ArchiveRecord's
/// Value field — every substrate-driven writer (Consolidator, Reflection)
/// asks for this same terse style, so a reader scanning archived facts sees
/// consistent density regardless of which agent wrote them. PromptCap.Apply
/// backs this up as a hard char-count limit for substrates that ignore it.
/// </summary>
public static class ArchiveWriteStyle
{
    public const string TerseValue = "1-5 content words, no filler — terse style, not a full sentence";

    /// <summary>
    /// Lookup is by pair, so the same fact stated in two languages would
    /// otherwise land on two pairs and never dedup. Normalizing the
    /// structural vocabulary to English at write time keeps one fact as one
    /// entry whatever language it arrived in. Proper nouns are carved out
    /// deliberately: translating a name or a place would corrupt the record
    /// itself, which is worse than the duplication this prevents.
    /// </summary>
    public const string EnglishFields = """
        Always write category, topic, subtopic and key in English, whatever
        language the turn was in — a fact stated in another language must land
        under the same English wording it would have had in English. Never
        translate proper nouns: names of people, places and organisations stay
        exactly as they were written, in subject and in value alike.
        """;
}

/// <summary>
/// Single-key state-blob storage — today's exact shape, unchanged. Used by
/// IdentityAgent's identity, ImpulseAgent's drive vectors, Governance's
/// frustration log, and Reflection's eagerness read. Deliberately distinct
/// from IArchiveStore: these are not knowledge-swarm facts.
/// </summary>
public interface IAgentStateStore
{
    Task<IReadOnlyList<AgentStateRecord>> LookupAsync(IReadOnlyList<string> paths, int maxPerPath, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<AgentStateRecord> records, CancellationToken cancellationToken);
}

public sealed record AgentStateRecord(string Path, string Content, DateTimeOffset Timestamp, string Domain = ArchiveDomain.External);
