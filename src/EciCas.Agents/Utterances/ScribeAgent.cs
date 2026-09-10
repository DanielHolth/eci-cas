using EciCas.Agents.Archivist;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

/// <summary>
/// The whole write path of the inverted archive: take what was said, keep it,
/// and then work out what it meant.
///
/// **Two stores, in that order, and the order is the design.** What was said
/// goes to <see cref="IUtteranceLog"/> first and unconditionally -- before the
/// filter, before the extractor, before any model has an opinion. Everything
/// after that is index: facts, vectors, threads, all of it derived, all of it
/// rebuildable from the line already on disk. A turn can lose its index and
/// get it back from <see cref="FactBackfill"/>. It cannot lose what was said.
///
/// **Why a substrate call reappeared on the write path.** The inversion
/// deleted two -- category and key, a closed vocabulary, a wrong guess
/// unreachable forever. This is not that. A ten-sentence paste holding
/// fifteen facts was one row with one centroid vector, which cleared no read
/// floor and, on the rare read it won, spent a slot on fourteen facts nobody
/// asked for. The extractor produces free text into a derived store, so a bad
/// extraction is a recomputation. See <see cref="SubstrateFactExtractor"/>.
///
/// **The persona's own thoughts are not kept.** Reflection pushes its ideas
/// back onto Perception, and those ideas came *out of* the archive. Storing
/// them back would recirculate the persona's own thought as a remembered fact
/// about the person, and give it a hit count for the trouble. Self already has
/// a home in the passage store. Its *replies* are kept, in their own log
/// under the input's turn number, and never become facts: they are there so
/// the extractor can see what "I totally agree" was agreeing with.
///
/// **It holds no slot.** Nothing in the turn depends on the write landing, so
/// this is absent from the bundle and no part of the reply waits on a disk
/// write or on the extractor. A failure here loses one index row and is
/// logged. It does announce what it kept, on SystemControl, the way Cataloger
/// did -- a notification rather than a slot, and the only reason the turn log
/// can say what the persona learned this turn.
/// </summary>
public sealed class ScribeAgent : AgentBase
{
    private readonly IMessageBus _bus;
    private readonly IUtteranceLog _utterances;
    private readonly IFactLog _facts;
    private readonly IFactExtractor _extractor;
    private readonly ThreadWeaver _weaver;
    private readonly UtteranceOptions _options;
    private readonly ILogger _logger;

    public ScribeAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ScribeAgent> logger,
        IUtteranceLog utterances, IFactLog facts, IFactExtractor extractor, ThreadWeaver weaver,
        IOptions<UtteranceOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _utterances = utterances;
        _facts = facts;
        _extractor = extractor;
        _weaver = weaver;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Scribe";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.Conclusion];

    /// <summary>The speaker every reply row is stamped with.</summary>
    public const string ReplySpeaker = "assistant";

    /// <summary>Open turns: the number and profile each input took, until its conclusion.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (long Turn, string? ProfileId)> _open = new();

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Topic == Topics.Conclusion)
        {
            await ConcludeAsync(envelope, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = (envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty).Trim();

        // The turn in progress is one past the concluded count, and the
        // count moves only when the turn concludes (ConcludeAsync). Self
        // turns take a number too: the denominator of every hit rate has to
        // count the turns that wanted nothing as honestly as the rest.
        var turn = _utterances.TurnsRecorded + 1;
        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        _open[envelope.CorrelationId] = (turn, profileId);

        if (text.Length == 0 || Self(envelope))
        {
            return;
        }

        var utterance = new Utterance(
            Id: Guid.NewGuid().ToString("n"),
            Text: text,
            Timestamp: envelope.Timestamp,
            Speaker: profileId ?? "user",
            ProfileId: profileId,
            Turn: turn);

        try
        {
            await _utterances.AppendAsync([utterance], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The one loss the design cannot absorb, so it is the one write
            // that gets its own log line -- and indexing an utterance that
            // was never stored would leave a fact whose source does not
            // exist, which is worse than a turn that went unrecorded.
            _logger.LogError(ex, "{Agent} could not keep what was said.", Name);
            return;
        }

        try
        {
            await IndexAsync(utterance, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Agent} kept the utterance but could not index it.", Name);
        }
    }

    /// <summary>
    /// Utterance to facts to rows: extract, filter, thread, file, announce.
    /// Everything in here is disposable by construction -- the utterance it
    /// was computed from is already on disk.
    /// </summary>
    private async Task IndexAsync(Utterance utterance, Envelope envelope, CancellationToken cancellationToken)
    {
        var previous = UtteranceContext.PreviousReply(
            await _utterances.RepliesAsync(cancellationToken).ConfigureAwait(false), utterance);
        var sentences = await _extractor.ExtractAsync(utterance, previous, cancellationToken).ConfigureAwait(false);
        var turnsNow = utterance.Turn;

        var facts = new List<Fact>(sentences.Count);
        foreach (var sentence in sentences)
        {
            var keywords = KeywordExtractor.Content(sentence);

            // Nothing to recall from a sentence with no content word in it,
            // and it would compete for one of five slots forever. This runs
            // per fact rather than per utterance now: a paste that mixes
            // "hey!" with three real claims used to be kept or dropped
            // whole, and can now lose only the greeting.
            if (!UtteranceFilter.Keep(keywords, _options))
            {
                continue;
            }

            facts.Add(new Fact(
                Id: Guid.NewGuid().ToString("n"),
                SourceId: utterance.Id,
                Text: sentence,
                Timestamp: utterance.Timestamp,
                Speaker: utterance.Speaker,
                ProfileId: utterance.ProfileId,
                Keywords: keywords,
                FirstSeenTurn: turnsNow));
        }

        if (facts.Count == 0)
        {
            return;
        }

        var woven = await _weaver.WeaveAsync(facts, cancellationToken).ConfigureAwait(false);
        await _facts.AppendAsync(woven.Rows, cancellationToken).ConfigureAwait(false);
        await _facts.UpdateDerivedAsync(woven.Retired, cancellationToken).ConfigureAwait(false);

        // The archive gets the row either way. The announcement is the UI's
        // "Learned", and on a turn that never reached the extractor the row
        // is just the utterance handed back -- announcing that would flood
        // the Thoughts panel with a restatement of what the person just
        // typed, once per turn, forever.
        if (_options.ExtractorEnabled)
        {
            // Still Archivist's constants, for the same reason Cataloger used
            // them: Identity and Impulse listen for "the archive grew", not
            // for whichever agent is holding the pen this month.
            var kept = (IReadOnlyList<string>)[.. woven.Rows.Select(row => row.Text)];
            _bus.Publish(Topics.SystemControl, envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
                MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ArchivistAgent.WrittenKind)
                    .With(ArchivistAgent.WrittenRecordsKey, kept)));
        }
    }

    /// <summary>
    /// The reply is kept under its input's turn number, then the global
    /// count advances and is saved. The reply write can fail without the
    /// count stalling: a skipped number would pair the next input with the
    /// wrong reply.
    /// </summary>
    private async Task ConcludeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var reply = (envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty).Trim();
        if (_open.TryRemove(envelope.CorrelationId, out var open) && reply.Length > 0)
        {
            try
            {
                await _utterances.AppendReplyAsync(new Utterance(
                    Id: Guid.NewGuid().ToString("n"),
                    Text: reply,
                    Timestamp: envelope.Timestamp,
                    Speaker: ReplySpeaker,
                    ProfileId: open.ProfileId,
                    Turn: open.Turn), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "{Agent} could not keep the reply.", Name);
            }
        }

        await _utterances.RecordTurnAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this perception is the persona talking to itself. Reflection
    /// stamps its own ideas on the way in; see the class remarks for why they
    /// stop here.
    /// </summary>
    private static bool Self(Envelope envelope) =>
        string.Equals(envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey), "self", StringComparison.Ordinal);
}
