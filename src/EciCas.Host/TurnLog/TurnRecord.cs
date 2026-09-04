using System.Text.Json.Serialization;

namespace EciCas.Host.TurnLog;

/// <summary>One substrate call, as the log shows it. Cost and tokens are null when the provider does not report them — the mock tier reports neither, and a rendered $0.0000 would read as free rather than unmeasured.</summary>
public sealed record SubstrateCall(string Agent, string Class, string? Label, double LatencyMs, int? Tokens, decimal? Cost, string? Degraded);

/// <summary>
/// What happened in one event, in the order a person reads it rather than
/// the order the bus produced it.
///
/// This is the whole contract between the projection and everything that
/// renders it — the SSE surface, the disk log, and anything later. It holds
/// strings, not envelopes: a consumer should not need the meta-key table to
/// display a line, and nothing downstream of here re-derives anything.
///
/// Empty collections and nulls mean "this faculty had nothing to say",
/// which is a slot the renderer skips rather than a slot it draws empty.
/// </summary>
public sealed record TurnRecord
{
    public required long Seq { get; init; }
    public required Guid CorrelationId { get; init; }

    /// <summary>Null for events nobody owns — the console loop, and Reflection's own batches.</summary>
    public string? ProfileId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }

    public string? Perception { get; init; }

    /// <summary>The persona talking to itself: a Reflection idea pushed back onto perception. Not a line the person typed, and must never be drawn as one.</summary>
    public bool SelfTriggered { get; init; }

    public string? Impulse { get; init; }

    /// <summary>Rows read out of the archive, at their full path. Recall does the reading; the surface files it under Librarian, which is the faculty a person asked for.</summary>
    public IReadOnlyList<string> Reads { get; init; } = [];

    public IReadOnlyList<string> Hindsight { get; init; } = [];
    public string? Intent { get; init; }

    /// <summary>Lower-cased verdict, carried only when it was not green — a green verdict is the absence of news.</summary>
    public string? Verdict { get; init; }

    public string? Concern { get; init; }
    public IReadOnlyList<string> Writes { get; init; } = [];
    public IReadOnlyList<string> Passages { get; init; } = [];
    public string? Idea { get; init; }
    public IReadOnlyList<SubstrateCall> Calls { get; init; } = [];

    /// <summary>True once an Action envelope has landed. A record can still grow after this — Archivist and Reflection publish behind the reply.</summary>
    public bool Concluded { get; init; }

    /// <summary>Null when no call reported a cost, rather than zero.</summary>
    [JsonInclude]
    public decimal? Cost => Calls.Any(c => c.Cost is not null) ? Calls.Sum(c => c.Cost ?? 0m) : null;

    /// <summary>
    /// Wall-clock across the event, not the sum of the calls. The fan-out is
    /// concurrent, so summing overstates it — the per-call numbers are the
    /// addends and this is the total they add up towards.
    /// </summary>
    [JsonInclude]
    public double WallClockMs => (EndedAt - StartedAt).TotalMilliseconds;
}
