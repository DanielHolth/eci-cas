namespace EciCas.Core;

/// <summary>
/// Knowledge-swarm archive: the semantic two-stage store (Reasoning selects
/// Category/Topic/Subtopic triples, Recall reads rows within a triple).
/// One record per fact, nine fields, all lowercase by convention.
/// </summary>
public interface IArchiveStore
{
    /// <summary>Distinct (Category, Topic, Subtopic) triples currently indexed, for Reasoning's selection prompt.</summary>
    IReadOnlyList<ArchiveTriple> Index { get; }

    Task<IReadOnlyList<ArchiveRecord>> LookupAsync(ArchiveTriple triple, int maxRows, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<ArchiveRecord> records, CancellationToken cancellationToken);
}

public sealed record ArchiveTriple(string Category, string Topic, string Subtopic);

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
    public ArchiveTriple Triple => new(Category, Topic, Subtopic);
}

public static class ArchiveDomain
{
    public const string External = "external";
    public const string Internal = "internal";
}

/// <summary>
/// Single-key state-blob storage — today's exact shape, unchanged. Used by
/// SelfAgent's identity, ImpulseAgent's drive vectors, Governance's
/// frustration log, and Reflection's eagerness read. Deliberately distinct
/// from IArchiveStore: these are not knowledge-swarm facts.
/// </summary>
public interface IAgentStateStore
{
    Task<IReadOnlyList<AgentStateRecord>> LookupAsync(IReadOnlyList<string> paths, int maxPerPath, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<AgentStateRecord> records, CancellationToken cancellationToken);
}

public sealed record AgentStateRecord(string Path, string Content, DateTimeOffset Timestamp, string Domain = ArchiveDomain.External);
