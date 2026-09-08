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

    /// <summary>
    /// The recency lane: the newest rows written anywhere in the archive,
    /// Timestamp-descending, unioned across the shared tier and this
    /// profile's own. Not a pair and not in the index — a second lane beside
    /// the shelf, always read, whatever Librarian selected.
    ///
    /// It exists because the shelf is weakest exactly where "lately" is
    /// asked: a fact filed an hour ago into a drawer no question names is
    /// unreachable until a question names the drawer. The lane is a derived
    /// view of rows the pair files already hold, so nothing here is the only
    /// copy of anything.
    /// </summary>
    Task<IReadOnlyList<ArchiveRecord>> RecentAsync(string? profileId, int limit, CancellationToken cancellationToken);

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
    double Importance = 0.5,
    string Sentence = "")
{
    public ArchivePair Pair => new(Category, Topic);

    /// <summary>
    /// The same fact as one plain sentence — "Daniel's passport expires in
    /// March 2027" for renewal/passport expiry = 2027-03. Written by the
    /// Archivist call that extracted the row, so it costs no extra call and
    /// is paid once rather than on every read.
    ///
    /// It exists because the address form is telegraphic and matches almost
    /// nothing a question says, and the stage that suffers for that is the
    /// one measured to lose the most: Recall discarding rows costs 18pp
    /// (RESULTS.md batch 12), more than pair selection or any shelf choice.
    /// More surface to match against helps a weak reader and a strong one
    /// for the same reason.
    ///
    /// Empty is normal, not broken: every row written before this column
    /// existed has none, and a substrate that omits the field still yields a
    /// usable fact. Readers fall back to the address form rather than
    /// dropping the row — see <see cref="Rendered"/>.
    /// </summary>
    public string Sentence { get; init; } = Sentence;

    /// <summary>
    /// How a row is put in front of a model: the address always, the
    /// sentence after it when there is one. Both, not either — the address
    /// carries the grouping a question may name explicitly, and dropping it
    /// for rows that happen to have a sentence would make a pair's rows
    /// render inconsistently within one prompt.
    /// </summary>
    public string Rendered =>
        Sentence.Length == 0
            ? $"{Subtopic} / {Subject} {Key} = {Value}"
            : $"{Subtopic} / {Subject} {Key} = {Value} — {Sentence}";
}

public static class ArchiveDomain
{
    public const string External = "external";
    public const string Internal = "internal";
}

/// <summary>
/// Shared prompt language for how much text belongs in an ArchiveRecord's
/// Value field — every substrate-driven writer (Archivist, Reflection)
/// asks for this same terse style, so a reader scanning archived facts sees
/// consistent density regardless of which agent wrote them. Asked, not
/// enforced: PromptCap used to truncate the value on the way in, which did
/// not prevent a long fact, it stored a mangled one. A validator may reject
/// a row; it may never edit one.
/// </summary>
public static class ArchiveWriteStyle
{
    public const string TerseValue = "1-5 keywords, or one terse sentence with no filler";

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
