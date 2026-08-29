using EciCas.Core;

namespace EciCas.Host;

/// <summary>
/// Wire shape for the SSE surface. Meta is flattened to a plain dictionary —
/// MetaBag.ToDictionary() is the one sanctioned escape hatch for this boundary.
/// </summary>
public sealed record EnvelopeDto(
    Guid EventId,
    Guid CorrelationId,
    string Topic,
    string PublishedBy,
    DateTimeOffset Timestamp,
    Severity Severity,
    int Generation,
    IReadOnlyDictionary<string, object?> Meta)
{
    public static EnvelopeDto From(Envelope envelope) => new(
        envelope.EventId,
        envelope.CorrelationId,
        envelope.Topic,
        envelope.PublishedBy,
        envelope.Timestamp,
        envelope.Severity,
        envelope.Generation,
        envelope.Meta.ToDictionary());
}
