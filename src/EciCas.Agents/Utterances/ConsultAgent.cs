using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

/// <summary>
/// The whole read path of the inverted archive, in one hop off Perception.
///
/// It replaces Librarian and Recall together -- a pair-selecting call and N
/// chunk-picking calls, four or five substrate round trips on the critical
/// path of every turn -- with one embed and a sweep. That is the inversion's
/// central claim: the routing existed to make a small corpus searchable by a
/// model, and a corpus with vectors does not need to be routed at all.
///
/// **It publishes Recall's key on purpose.** Intent and TurnProjection read
/// <see cref="RecalledFactsKey"/>, and neither has any business
/// knowing which archive answered. So the utterances are handed over as
/// <see cref="ArchiveRecord"/> with the sentence carried in Sentence and the
/// address left empty -- the address is what the inversion deleted, and
/// fabricating one here would smuggle the shelf back in as a rendering
/// detail. The blast radius of the whole swap is therefore this file, the
/// scribe, and a registration flag.
/// </summary>
public sealed class ConsultAgent : AgentBase
{
    private readonly IMessageBus _bus;
    private readonly FactConsult _consult;
    private readonly FactPicker _picker;
    private readonly UtteranceOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// The meta key carrying recalled facts, kept under Recall's original
    /// name since Intent and TurnProjection read it without caring which
    /// archive answered.
    /// </summary>
    public const string RecalledFactsKey = "recall.facts";

    public ConsultAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ConsultAgent> logger, FactConsult consult,
        FactPicker picker, IOptions<UtteranceOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _consult = consult;
        _picker = picker;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Recall's name, because it holds Recall's slot in the roster and
    /// answers Recall's question. Governance counts slots, not
    /// implementations.
    /// </summary>
    public override string Name => "Recall";

    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var text = (envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty).Trim();

        IReadOnlyList<Consulted> hits = [];
        try
        {
            hits = _options.PickerEnabled
                ? await FanoutAsync(text, cancellationToken).ConfigureAwait(false)
                : await _consult.FindAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A read that failed is a turn with no memory, not a broken turn.
            // The slot is filled below either way, because a silent slot
            // holds the bundle open until Governance times it out.
            _logger.LogWarning(ex, "{Agent} could not consult the facts.", Name);
        }

        _logger.LogInformation("{Agent} {Facts}", Name,
            hits.Count == 0 ? "nothing on file" : string.Join(" | ", hits.Select(h => $"{h.Row.Text} ({h.Score:0.00})")));

        var facts = (IReadOnlyList<ArchiveRecord>)[.. hits.Select(ToRecord)];
        var meta = MetaBag.Empty.With(RecalledFactsKey, facts);
        _bus.Publish(Topics.Advisories, envelope.Derive(Topics.Advisories, Name, envelope.Severity, meta));
    }

    /// <summary>
    /// Wide shortlist, then the picker. A picker that could not be asked
    /// leaves the cosine order standing, cut to the usual depth, so a
    /// substrate outage costs the turn its judgment and not its memory.
    /// </summary>
    private async Task<IReadOnlyList<Consulted>> FanoutAsync(string text, CancellationToken cancellationToken)
    {
        var shortlist = await _consult.ShortlistAsync(text, _options.FanoutWidth, cancellationToken).ConfigureAwait(false);
        var picked = await _picker.PickAsync(text, shortlist, _options.PickMax, cancellationToken).ConfigureAwait(false);
        var used = picked ?? [.. shortlist.Take(_consult.Depth)];
        _logger.LogInformation("{Agent} picked {Picked} of {Shortlist}{Fallback}", Name, used.Count, shortlist.Count,
            picked is null ? " (cosine fallback)" : string.Empty);
        await _consult.RecordAsync(used, cancellationToken).ConfigureAwait(false);
        return used;
    }

    /// <summary>
    /// The fact, wearing the shape Intent already reads. Everything the
    /// old record carried about *where* a fact lived is left empty, and
    /// Importance is the read's own score -- the one number here that still
    /// means what it used to.
    /// </summary>
    private static ArchiveRecord ToRecord(Consulted hit) => new(
        Category: string.Empty,
        Topic: string.Empty,
        Subtopic: string.Empty,
        Subject: string.Empty,
        Key: string.Empty,
        Value: string.Empty,
        Timestamp: hit.Row.Timestamp,
        Domain: ArchiveDomain.External,
        Importance: Math.Round(hit.Score, 2),
        Sentence: hit.Row.Text);
}
