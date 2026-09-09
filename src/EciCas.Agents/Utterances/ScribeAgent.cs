using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

/// <summary>
/// The whole write path of the inverted archive: take what was said, and
/// keep it.
///
/// This replaces Archivist and Cataloger both, and the difference is the
/// point of the inversion. Those two spent a substrate call each deciding
/// what a sentence *was* -- which category, which topic, which subject, which
/// key -- before anything could be stored, so a sentence nobody could file
/// was a sentence nobody kept. Here the sentence is the record. Filing is a
/// derived column, computed later, wrong at no cost, and rebuildable from a
/// log that never needed it.
///
/// **No substrate call on this path.** The keyword extractor is a regex and
/// the threader is a dot product. What may cost a call is the consolidator,
/// and it is gated, off by default, and downstream of a shortcut that
/// absorbs most of the volume.
///
/// **It publishes nothing.** Nothing in the turn depends on the write
/// landing, so this holds no slot in the bundle and no part of the reply
/// waits on a disk write. A failure here loses one utterance and is logged;
/// it does not degrade a turn that has already been answered.
/// </summary>
public sealed class ScribeAgent : AgentBase
{
    private readonly IUtteranceLog _log;
    private readonly ThreadWeaver _weaver;
    private readonly ILogger _logger;

    public ScribeAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ScribeAgent> logger,
        IUtteranceLog log, ThreadWeaver weaver)
        : base(bus, activity, logger)
    {
        _log = log;
        _weaver = weaver;
        _logger = logger;
    }

    public override string Name => "Scribe";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = (envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty).Trim();

        // The turn counter advances first and unconditionally. It is the
        // denominator of every hit rate in the corpus, and it has to count
        // the turns that wanted nothing as honestly as the ones that wanted
        // something -- a rate measured only over turns that recalled
        // something is not a rate, it is a tautology.
        await _log.RecordTurnAsync(cancellationToken).ConfigureAwait(false);

        if (text.Length == 0)
        {
            return;
        }

        var utterance = new Utterance(
            Id: Guid.NewGuid().ToString("n"),
            Text: text,
            Timestamp: envelope.Timestamp,
            Speaker: envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) ?? "user",
            ProfileId: envelope.Meta.Get<string>(PerceptionAgent.ProfileKey),
            Keywords: KeywordExtractor.Content(text));

        try
        {
            var woven = await _weaver.WeaveAsync([utterance], cancellationToken).ConfigureAwait(false);
            await _log.AppendAsync(woven.Rows, cancellationToken).ConfigureAwait(false);
            await _log.UpdateDerivedAsync(woven.Retired, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Agent} could not keep an utterance.", Name);
        }
    }
}
