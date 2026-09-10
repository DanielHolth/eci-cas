namespace EciCas.Core;

/// <summary>
/// One thing somebody said. All of it is ground truth, and there is nothing
/// else on the row.
///
/// This used to carry its own keywords, vector, thread and hit count, because
/// there was one file and a row had to be both the record and the index. The
/// index now lives in <see cref="Fact"/>, in its own store, and what is left
/// here is the sentence and its provenance: appended, never rewritten,
/// readable in 2126 by anyone with a parquet reader and none of our code.
///
/// The split is what makes the derived side safe to be wrong about. A bad
/// extraction, a swapped embedding model, a threading bug in 2030 -- delete
/// <c>facts/</c> and rebuild it from this. Nothing here ever has to move.
///
/// The same shape holds the persona's replies, in their own log. <c>Turn</c>
/// is the global turn counter at the moment the input arrived; a reply
/// carries its input's turn, so the two logs pair up by it.
/// </summary>
public sealed record Utterance(
    string Id,
    string Text,
    DateTimeOffset Timestamp,
    string Speaker,
    string? ProfileId,
    long Turn = 0);

/// <summary>
/// Append-only log of what was said, sharded by month.
///
/// **Nothing routes on a shard.** Retrieval never touches this store at all
/// now; a shard is a size bound and a backup unit, which is why there is no
/// shard-selection method here to be tempted by.
///
/// **Nothing here is ever updated.** There is no derived-column write and no
/// hit counter, because there is nothing derived on the row. That is not a
/// convenience -- an append-only file with no update path cannot be corrupted
/// by a bug in a rewrite that was never written.
/// </summary>
public interface IUtteranceLog
{
    /// <summary>Ground truth in, nothing out. Never updates, never deletes.</summary>
    Task AppendAsync(IReadOnlyList<Utterance> utterances, CancellationToken cancellationToken);

    /// <summary>
    /// Everything, oldest first. Read by the backfill when it rebuilds the
    /// fact store, and by nothing on the turn's path.
    /// </summary>
    Task<IReadOnlyList<Utterance>> AllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What the persona said back, one row per concluded turn, in its own
    /// log. Kept so the extractor can read what an input was answering:
    /// "I totally agree" states a fact only next to the reply it agreed with.
    /// </summary>
    Task AppendReplyAsync(Utterance reply, CancellationToken cancellationToken);

    /// <summary>Every reply, oldest first.</summary>
    Task<IReadOnlyList<Utterance>> RepliesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The global turn counter, kept outside the shards -- the denominator of
    /// every hit rate, and the ordinal a new fact stamps as its first-seen.
    /// It lives here rather than on the fact store because it counts turns,
    /// and a turn is a thing that was said, not a thing that was derived.
    /// </summary>
    long TurnsRecorded { get; }

    /// <summary>
    /// Called once per concluded turn: the counter advances by one and is
    /// saved. An input takes the current value, and its reply takes the same.
    /// </summary>
    Task<long> RecordTurnAsync(CancellationToken cancellationToken);
}

public static class UtteranceContext
{
    /// <summary>
    /// The reply an input was answering: the newest reply from an earlier
    /// turn that this person could have seen (theirs, or one with no
    /// profile). One rule for the live write and the rebuild.
    /// </summary>
    public static string? PreviousReply(IReadOnlyList<Utterance> replies, Utterance input)
    {
        if (input.Turn <= 0)
        {
            return null;
        }

        for (var i = replies.Count - 1; i >= 0; i--)
        {
            var reply = replies[i];
            if (reply.Turn < input.Turn && (reply.ProfileId is null || reply.ProfileId == input.ProfileId))
            {
                return reply.Text;
            }
        }

        return null;
    }
}
