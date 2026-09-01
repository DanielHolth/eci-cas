using EciCas.Core;

namespace EciCas.Host;

/// <summary>
/// Wire shape for the SSE surface. Meta is flattened to a plain dictionary —
/// MetaBag.ToDictionary() is the one sanctioned escape hatch for this boundary.
///
/// Some keys are load-bearing on the bus and pure weight at the HTTP edge:
/// `intent.prompt` is the whole composed prompt, by far the largest value in
/// play, and it rides three envelopes a turn (proposal, verdict, action)
/// while the companion reads it never. A deny-list rather than an allow-list
/// on purpose — an allow-list would need editing every time an agent adds a
/// key the UI wants, and forgetting would silently break a feature instead
/// of merely shipping bloat.
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
    public static EnvelopeDto From(Envelope envelope, IReadOnlySet<string> excludedMetaKeys) => new(
        envelope.EventId,
        envelope.CorrelationId,
        envelope.Topic,
        envelope.PublishedBy,
        envelope.Timestamp,
        envelope.Severity,
        envelope.Generation,
        Filtered(envelope.Meta.ToDictionary(), excludedMetaKeys));

    private static IReadOnlyDictionary<string, object?> Filtered(
        IReadOnlyDictionary<string, object?> meta, IReadOnlySet<string> excluded) =>
        excluded.Count == 0
            ? meta
            : meta.Where(pair => !excluded.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
}
