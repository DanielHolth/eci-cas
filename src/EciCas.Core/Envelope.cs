namespace EciCas.Core;

public sealed record Envelope
{
    public required Guid EventId { get; init; }

    /// <summary>
    /// Ties every envelope descended from one originating turn together —
    /// e.g. an advisory and the perception event it responds to. Set once by
    /// Create() and carried forward unchanged by Derive().
    /// </summary>
    public required Guid CorrelationId { get; init; }

    public required string Topic { get; init; }
    public required string PublishedBy { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Severity Severity { get; init; }
    public MetaBag Meta { get; init; } = MetaBag.Empty;

    /// <summary>
    /// How many times this arc has fed itself. Derive() carries it across a
    /// turn unchanged; Reflection is the only agent that increments it, when
    /// it pushes one of its own ideas back onto events.perception as if it
    /// were external input. Not EventId chaining — this is the loop guard
    /// for exactly that push, checked against MaxIdeaGeneration. Any future
    /// agent that feeds its own output back in owes the same increment.
    /// </summary>
    public int Generation { get; init; }

    public static Envelope Create(string topic, string publishedBy, Severity severity, MetaBag? meta = null, int generation = 0)
    {
        var id = Guid.NewGuid();
        return new()
        {
            EventId = id,
            CorrelationId = id,
            Topic = topic,
            PublishedBy = publishedBy,
            Timestamp = DateTimeOffset.UtcNow,
            Severity = severity,
            Meta = meta ?? MetaBag.Empty,
            Generation = generation,
        };
    }

    /// <summary>Publishes a response to this envelope: new EventId, same CorrelationId.</summary>
    public Envelope Derive(string topic, string publishedBy, Severity severity, MetaBag? meta = null) =>
        new()
        {
            EventId = Guid.NewGuid(),
            CorrelationId = CorrelationId,
            Topic = topic,
            PublishedBy = publishedBy,
            Timestamp = DateTimeOffset.UtcNow,
            Severity = severity,
            Meta = meta ?? MetaBag.Empty,
            Generation = Generation,
        };
}
