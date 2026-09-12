namespace EciCas.Core;

/// <summary>
/// An agent name resolved to a concrete completion: the caller says who is
/// asking and the registry looks up what backs that agent in the running
/// tier. The tier is a manifest/DI choice — a mock is a substrate, not a
/// separate agent class.
/// </summary>
public interface ISubstrateProvider
{
    Task<SubstrateResult> CompleteAsync(string agent, string prompt, CancellationToken cancellationToken);
}

/// <param name="Provider">Which endpoint served it — "local", "openai", "mistral", "mock".
/// Optional because a stub has no endpoint, but a real provider must fill it:
/// the whole reason a person opens the debug panel on a mixed tier is to see
/// whether a given agent went out to a vendor or stayed on their own GPU.</param>
/// <param name="Model">The vendor model id actually sent on the wire.</param>
public sealed record SubstrateResult(
    string Text,
    TimeSpan Latency,
    int? TokenCount,
    decimal? Cost,
    string? Provider = null,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

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

    /// <summary>
    /// True only when this cancellation is the host going away — the caller's
    /// own token is the one that fired.
    ///
    /// Every substrate caller used to swallow the whole OperationCanceledException
    /// family with <c>when (ex is not OperationCanceledException)</c>, on the
    /// reading that a cancelled call is a shutdown and shutdown is not a
    /// failure worth naming. But HttpClient.Timeout throws
    /// TaskCanceledException, which is one of that family, so a call that
    /// simply took longer than Providers:TimeoutMs left the catch untaken:
    /// no degraded mark, no telemetry, no fallback published, nothing in the
    /// turn log. Classify has said "timed out" since it was written and could
    /// not be reached from any of them.
    ///
    /// Measured on 2026-09-09: Archivist made no substrate call on any of
    /// twelve turns and published no facts envelope, so Cataloger — which
    /// counts turns, not facts — never flushed either, and nothing about the
    /// person reached the archive for the whole session while the persona's
    /// own reflection passages wrote normally.
    /// </summary>
    public static bool IsShutdown(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

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
