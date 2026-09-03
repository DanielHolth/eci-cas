using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Hindsight;

/// <summary>
/// Recall reads facts. Hindsight reads what the persona made of them.
///
/// A thought note is written by Reflection after a batch of turns, for no
/// one, about what those turns made the persona notice. Hindsight's job is
/// to wake one when a prompt brushes against it — months later if that is
/// when it fits — and hand it to Intent in its own bundle slot, as the
/// persona's own voice rather than as a fact.
///
/// Why an agent and not a helper inside Librarian, where this used to live:
/// prose and facts are different substances, and riding Librarian's
/// envelope into Recall's roster slot laundered one through the other.
/// Intent now weighs "what the archive held" and "what I once thought about
/// this" as two separate contributions and can disagree with either.
///
/// Deterministic tier: an embed and a cosine sweep, no substrate call and
/// no tier entry, so no CognitiveAgent&lt;T&gt; base. That is also why it
/// can join the roster for free — it adds a bundle slot, not a turn cost.
///
/// A hit is a lead, not an answer. The floor is deliberately low (see
/// PassageOptions.MinScore): notes that restate the prompt tell the persona
/// what it already knew, and unrelated material is what keeps a thought
/// from resonating with itself. See roadmap.md, "Hindsight — what it is
/// for", for the ring this sits inside and why the pairs field matters.
/// </summary>
public sealed class HindsightAgent : AgentBase
{
    /// <summary>
    /// Woken notes, newest thought first by relevance rather than by clock.
    /// Read straight off the bundle by Intent — Governance folds every
    /// advisory's meta into the bundle's, so this needs no carrier.
    /// </summary>
    public const string NotesKey = "hindsight.notes";

    /// <summary>
    /// Ids of the notes woken this turn, parallel to <see cref="NotesKey"/>.
    /// Not for reading — for parentage: whatever Reflection later writes
    /// about this turn descends from these, and recording that is the only
    /// way to see the ring (Hindsight -> Intent -> reply -> note ->
    /// Hindsight) from the outside once it is turning.
    /// </summary>
    public const string NoteIdsKey = "hindsight.note_ids";

    /// <summary>
    /// The deepest echo depth among the notes woken this turn. Published
    /// rather than looked up later so Reflection can stamp its new note at
    /// depth+1 without a second corpus read, and so the number travels the
    /// same hop as the ids it belongs to.
    ///
    /// A diagnostic, never a retrieval input. The moment a shallow note is
    /// preferred over a deep one, the persona can lower its own echo depth
    /// by writing thoughts with no history — which is exactly the behaviour
    /// the number exists to detect.
    /// </summary>
    public const string EchoDepthKey = "hindsight.echo_depth";

    private readonly IMessageBus _bus;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IPassageStore _passages;
    private readonly PassageOptions _options;
    private readonly ILogger _logger;

    public HindsightAgent(IMessageBus bus, BusActivityTracker activity, ILogger<HindsightAgent> logger,
        IEmbeddingProvider embeddings, IPassageStore passages, IOptions<PassageOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _embeddings = embeddings;
        _passages = passages;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Hindsight";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var woken = await WakeAsync(PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey)), cancellationToken).ConfigureAwait(false);

        // Published even when empty, and even when there is no embedder at
        // all: Hindsight is a roster slot now, and a slot that stays silent
        // holds the whole bundle open until Governance times it out. Having
        // thought nothing about a turn is a normal answer.
        var meta = MetaBag.Empty;
        if (woken.Count > 0)
        {
            meta = meta
                .With(NotesKey, (IReadOnlyList<string>)[.. woken.Select(h => $"{Age(h.Passage.Timestamp)}: {h.Passage.Text}")])
                .With(NoteIdsKey, (IReadOnlyList<string>)[.. woken.Select(h => h.Passage.Id)])
                .With(EchoDepthKey, woken.Max(h => h.Passage.EchoDepth));
        }

        _bus.Publish(Topics.Advisories, envelope.Derive(Topics.Advisories, Name, envelope.Severity, meta));
    }

    /// <summary>
    /// No embedder is a normal state, not a degradation — the persona is
    /// thinking without a shortcut it may never have had, and nothing here
    /// marks the turn impaired. Same posture SearchPassagesAsync held when
    /// this lived in Librarian.
    /// </summary>
    private async Task<IReadOnlyList<PassageHit>> WakeAsync(string text, CancellationToken cancellationToken)
    {
        if (!_embeddings.Available || string.IsNullOrWhiteSpace(text) || _options.TopK <= 0)
        {
            _logger.LogDebug("{Agent} did not search: embeddings available {Available}, topK {TopK}", Name, _embeddings.Available, _options.TopK);
            return [];
        }

        var query = await _embeddings.EmbedAsync([text], cancellationToken).ConfigureAwait(false);
        if (query.Count == 0)
        {
            return [];
        }

        var hits = await _passages.SearchAsync(query[0], _options.TopK, _options.MinScore, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            _logger.LogDebug("{Agent} woke nothing for \"{Text}\" (topK {TopK}, min score {MinScore})", Name, text, _options.TopK, _options.MinScore);
            return [];
        }

        _logger.LogInformation("{Agent} woke {Count} note(s), deepest echo {Depth}: {Texts}",
            Name, hits.Count, hits.Max(h => h.Passage.EchoDepth), string.Join(" | ", hits.Select(h => h.Passage.Text)));
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} hits for \"{Text}\": {Hits}", Name, text,
                string.Join(" | ", hits.Select(h => $"{h.Score:F3} [{h.Passage.Id}] depth {h.Passage.EchoDepth} {h.Passage.Text} -> [{string.Join(", ", h.Passage.Pairs.Select(p => $"{p.Category}/{p.Topic}"))}]")));
        }

        return hits;
    }

    /// <summary>
    /// A note reaches Intent with its age on the front. Without it a thought
    /// from months ago and one from this morning read identically, and the
    /// difference between a recollection and an echo of the last turn is
    /// exactly what is worth knowing about a woken note. Coarse and
    /// qualitative on purpose — the score stays out, because a number invites
    /// the model to trust a close match over a sideways one, and the sideways
    /// ones are why the floor is low.
    ///
    /// Reads the passage timestamp, which a revisit deliberately preserves:
    /// a sharpened thought keeps the age of the thought it sharpens.
    /// </summary>
    private static string Age(DateTimeOffset written)
    {
        var span = DateTimeOffset.UtcNow - written;
        return span switch
        {
            { TotalHours: < 12 } => "earlier today",
            { TotalHours: < 36 } => "yesterday",
            { TotalDays: < 14 } => $"{(int)span.TotalDays} days ago",
            { TotalDays: < 60 } => $"{(int)(span.TotalDays / 7)} weeks ago",
            { TotalDays: < 365 } => $"{(int)(span.TotalDays / 30)} months ago",
            { TotalDays: < 730 } => "over a year ago",
            _ => $"{(int)(span.TotalDays / 365)} years ago",
        };
    }
}
