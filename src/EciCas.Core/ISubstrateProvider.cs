namespace EciCas.Core;

/// <summary>
/// Logical substrate class (fast-low/fast-medium/fast-high, slow-low/slow-medium/slow-high)
/// resolved to a concrete completion. The tier is a manifest/DI choice — a mock
/// is a substrate, not a separate agent class.
/// </summary>
public interface ISubstrateProvider
{
    Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken);
}

public sealed record SubstrateResult(string Text, TimeSpan Latency, int? TokenCount, decimal? Cost);

/// <summary>
/// How a substrate failure is named on the bus, so a turn concluded with
/// half the persona's faculties missing can say so.
///
/// A fallback that is published *unmarked* is the dangerous case: when
/// Librarian and Recall fail but Intent succeeds, the person gets a fluent,
/// confident, entirely ungrounded answer and no signal that anything went
/// wrong. Marking is every substrate caller's job; deciding what to say
/// about it is Governance's alone, since it is the only agent that sees the
/// whole fan-out.
///
/// Deliberately a short human-readable cause rather than an enum: it is
/// written straight into the notice a person reads, and the set of ways a
/// network fails is not closed.
/// </summary>
public static class SubstrateHealth
{
    /// <summary>Meta key carrying the cause. Present means this result is a fallback, not a thought.</summary>
    public const string DegradedKey = "substrate.degraded";

    public const string Unreachable = "unreachable";
    public const string TimedOut = "timed out";
    public const string Refused = "refused";

    /// <summary>No-ops when the call succeeded, so callers need no branch of their own.</summary>
    public static MetaBag Mark(MetaBag meta, string? cause) =>
        cause is null ? meta : meta.With(DegradedKey, cause);

    /// <summary>
    /// One short phrase in place of a stack trace. A single offline turn
    /// otherwise prints four or five near-identical traces and scrolls the
    /// actual warning away — they say less than one line of classification.
    /// </summary>
    public static string Classify(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => TimedOut,
        HttpRequestException { StatusCode: not null } http => $"{Refused} ({(int)http.StatusCode})",
        HttpRequestException or System.Net.Sockets.SocketException => Unreachable,
        _ => ex.GetType().Name,
    };
}
