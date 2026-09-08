using System.Diagnostics;
using System.Globalization;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Librarian;

/// <summary>
/// Was Reasoning until the name was audited against the job: this agent
/// reasons about nothing and reads no fact. It interprets the turn and
/// points Recall at a shelf — the catalogue half of a librarian, never the
/// fetching half, which is Recall's. "Reasoning" also overclaimed into
/// Intent's territory, and "Retrieval" would have collided with Recall,
/// which is the agent that actually retrieves.
///
/// Selector only: picks which of the archive's known (Category, Topic)
/// pairs might hold background relevant to this turn. It runs on every
/// turn with a non-empty index, including one small enough that selecting
/// everything would have been correct: skipping it there saved a call but
/// meant the selector's judgment was never exercised until the archive was
/// already too big to reason about by eye, and near-duplicate pairs — the
/// thing selection has to tell apart — appear long before that. Subtopic is
/// deliberately absent from what it is shown: resolving which subtopic
/// matters is Recall's job, reading actual rows, and keeping it out here
/// keeps the selection prompt short as the archive deepens. No advice
/// text published anymore — Intent owns all advisory framing; Recall is
/// the one that actually reads rows once a pair is selected.
///
/// Doesn't use CognitiveAgent&lt;T&gt;'s BuildPrompt template method: the
/// selection prompt needs the store's cached index read first, which
/// BuildPrompt's synchronous signature can't express. HandleAsync is
/// overridden directly instead, replicating the substrate-call/log/fallback
/// shape inline. BuildPrompt/ParseResult/FallbackResult below exist only to
/// satisfy CognitiveAgent&lt;T&gt;'s abstract contract and are never invoked.
/// </summary>
public sealed class LibrarianAgent : CognitiveAgent<IReadOnlyList<ArchivePair>>
{
    /// <summary>Selected pairs, carried on the events.selected-pairs envelope's Meta.</summary>
    public const string SelectedPairsKey = "librarian.selected_pairs";

    /// <summary>
    /// This turn's question as a vector, carried to Recall so it can rank the
    /// rows inside a pair without embedding the same sentence a second time.
    /// Absent whenever the embedder is unavailable, which Recall treats as
    /// "no ranking" rather than as an error.
    /// </summary>
    public const string QueryVectorKey = "librarian.query_vector";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly LibrarianOptions _options;
    private readonly RuntimeKnobs _knobs;

    /// <summary>
    /// Null when cataloger.txt has no gloss section, which is a shelf with no
    /// definitions rather than a fault: every option line simply reads as it
    /// did before. Lazy because the instruction store is loaded before any
    /// turn arrives and re-parsing 160 lines per selection would be waste.
    /// </summary>
    private readonly Lazy<TopicGloss?> _gloss;
    private readonly IEmbeddingProvider _embeddings;
    private readonly PairVectorIndex _pairVectors;
    private readonly IPassageStore _passages;
    private readonly PassageOptions _passageOptions;
    private readonly ILogger _logger;

    public LibrarianAgent(IMessageBus bus, BusActivityTracker activity, ILogger<LibrarianAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<LibrarianOptions> options,
        RuntimeKnobs knobs,
        IEmbeddingProvider embeddings, IPassageStore passages, IOptions<PassageOptions> passageOptions,
        IInstructionStore instructions)
        : base(bus, activity, logger, substrate, agentSubstrates)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _gloss = new Lazy<TopicGloss?>(() =>
        {
            try
            {
                return TopicGloss.Parse(_instructions.For(GlossOwner, GlossSection));
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        });
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _knobs = knobs;
        _embeddings = embeddings;
        _pairVectors = new PairVectorIndex(embeddings);
        _passages = passages;
        _passageOptions = passageOptions.Value;
        _logger = logger;
    }

    public override string Name => "Librarian";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception];

    protected override FallbackPosture Fallback => FallbackPosture.Open;

    protected override string BuildPrompt(Envelope envelope) =>
        throw new NotSupportedException($"{Name} overrides HandleAsync directly — see class remarks.");

    protected override IReadOnlyList<ArchivePair> ParseResult(SubstrateResult result) => [];

    protected override IReadOnlyList<ArchivePair> FallbackResult(Envelope envelope) => [];

    /// <summary>
    /// Never reached: HandleAsync is overridden, so nothing in the base class
    /// ever calls this. Kept to satisfy CognitiveAgent<T>, and delegating
    /// rather than throwing so a future base-class path degrades to "no
    /// passages" instead of an exception.
    /// </summary>
    protected override void Publish(Envelope envelope, string prompt, IReadOnlyList<ArchivePair> result, SubstrateResult? diagnostics, string? degraded) =>
        Publish(envelope, result, degraded);

    private void Publish(Envelope envelope, IReadOnlyList<ArchivePair> result, string? degraded, float[]? queryVector = null)
    {
        // Always published, even empty on fallback/no-index/no-signal text —
        // Recall's roster slot in Governance's bundle needs a reply every
        // time, or the bundle would only ever complete via timeout.
        //
        // TextKey is carried forward explicitly: Envelope.Derive starts a
        // fresh Meta rather than merging the parent's, so without this
        // Recall's picking prompt would have no idea what was actually asked
        // and could only rank candidates by generic importance — which is how
        // an unrelated "assistant/.../name" row used to outrank everything for
        // a question about the human's own name.
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
        var meta = SubstrateHealth.Mark(MetaBag.Empty.With(SelectedPairsKey, result).With(PerceptionAgent.TextKey, text), degraded);

        // The query vector rides along for the same reason the text does, and
        // for one more: Recall would otherwise embed the identical sentence
        // again on the same turn. The caching provider would make that cheap
        // rather than free, and a second embed is a second chance for the two
        // halves of one turn to disagree about what was asked.
        if (queryVector is { Length: > 0 })
        {
            meta = meta.With(QueryVectorKey, queryVector);
        }

        // The profile rides along for the same reason: it decides which
        // archive tier Recall reads, and Derive would otherwise drop it.
        if (envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) is { Length: > 0 } profileId)
        {
            meta = meta.With(PerceptionAgent.ProfileKey, profileId);
        }

        var selection = envelope.Derive(Topics.SelectedPairs, Name, envelope.Severity, meta);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} selected {Count} pair(s): {Pairs}", Name, result.Count,
                result.Count == 0 ? "none" : string.Join(", ", result.Select(p => $"{p.Category}/{p.Topic}")));
        }

        _bus.Publish(Topics.SelectedPairs, selection);
    }

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var index = _store.IndexFor(envelope.Meta.Get<string>(PerceptionAgent.ProfileKey));
        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey));

        // The count at Information, the pairs at Debug. An index of one pair
        // is a seeded-empty archive, i.e. the host was started from the wrong
        // folder (see docs/appendix.md), and that has already been debugged
        // as a Recall fault once — so the tell has to be visible at the level
        // someone is actually running when they get confused. The count is
        // free; the join is not, hence the guard on the detail line only.
        _logger.LogInformation("{Agent} index holds {Count} pair(s)", Name, index.Count);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} index: {Pairs}", Name,
                string.Join(", ", index.Select(p => $"{p.Category}/{p.Topic}")));
        }

        // The vector half, and it runs first because it is the cheap one: a
        // local embedding and a cosine sweep, no substrate call and no tier.
        // Whatever it finds is merged into the selection below rather than
        // replacing it — a passage says "last time this came up I should have
        // read X", which is a lead, not a verdict on everything else.
        // Embedded once, as a question rather than as stored material: on the
        // shipped e5 the two roles are different encoders, and everything this
        // vector is compared against - passages, pair glosses, archive rows -
        // was embedded as a passage.
        var queryVector = await EmbedQueryAsync(text, cancellationToken).ConfigureAwait(false);

        var remembered = await SearchPassageLeadsAsync(queryVector, index, cancellationToken).ConfigureAwait(false);

        // The pair layer's own vector cut, merged in beside the passage leads
        // and ahead of neither: a gloss match says this shelf is about what
        // was asked, which is a lead of exactly the same standing.
        if (queryVector is not null && VectorLaneRoom(remembered) > 0)
        {
            var near = await _pairVectors
                .NearestAsync(queryVector, Shown(index), _gloss.Value, VectorLaneRoom(remembered), _options.VectorMinScore, cancellationToken)
                .ConfigureAwait(false);

            if (near.Count > 0)
            {
                _logger.LogInformation("{Agent} vector leads: {Pairs}", Name,
                    string.Join(", ", near.Select(p => $"{p.Category}/{p.Topic}")));
                remembered = Merge(remembered, near);
            }
        }

        // An empty archive and a deliberately deterministic agent reach the
        // same place — nothing to select — and neither is a degradation. The
        // passages still go out: matching one costs no substrate call, so
        // there is no reason a deterministic Librarian should lose them.
        if (index.Count == 0 || !entry.UseSubstrate)
        {
            Publish(envelope, remembered, degraded: null, queryVector);
            return;
        }

        // What the selector is offered is not the whole index: see Shown.
        var shown = Shown(index);
        if (shown.Count == 0)
        {
            // An archive holding nothing but "other" pairs has nothing to
            // offer a selector, and an empty options list makes the prompt a
            // question with no answers.
            Publish(envelope, remembered, degraded: null, queryVector);
            return;
        }

        var prompt = BuildSelectionPrompt(text, shown);
        _logger.LogDebug("{Agent} selection prompt >>>\n{Prompt}", Name, prompt);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _substrate.CompleteAsync(entry.Class, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call [{Class}]: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, entry.Class, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            _logger.LogDebug("{Agent} selection response <<<\n{Response}", Name, result.Text);
            SubstrateTrace.Publish(_bus, envelope, Name, entry.Class, result);
            Publish(envelope, Merge(WithOverflow(ParsePairs(result.Text, shown), index), remembered), degraded: null, queryVector);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = SubstrateHealth.Classify(ex);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.LogWarning("{Agent} substrate call {Cause} after {LatencyMs}ms, fallback posture {Posture}",
                Name, cause, elapsed, Fallback);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, entry.Class, elapsed, cause);

            // Open posture, and the passages are exactly what makes it worth
            // something: a selection call that failed still leaves the turn
            // with whatever the persona previously learned to look up here.
            Publish(envelope, remembered, cause, queryVector);
        }
    }

    /// <summary>
    /// Cosine top-K over the passage corpus, keeping only the archive pairs
    /// those notes named. The note TEXT is not read here — that is
    /// HindsightAgent's, and it reaches Intent as its own bundle slot. What
    /// stays behind is the lead: "when something like this came up, I wished
    /// I had read person/family", which is a statement about where the facts
    /// are and therefore Librarian's business.
    ///
    /// The cost of the split is one extra embed per turn, since Hindsight
    /// embeds the same text for its own sweep. A local ONNX call, and worth
    /// it rather than having one agent's envelope carry the other's payload;
    /// sharing a per-turn embedding is a later optimisation (roadmap.md).
    ///
    /// Unavailable embeddings are a normal state, not a failure: no model
    /// downloaded, provider set to "none", or an embedding endpoint that just
    /// refused. All of them return nothing here and the turn proceeds on the
    /// pre-vector path, which is why this is not marked substrate.degraded —
    /// the persona is not thinking with a faculty missing, it is thinking
    /// without a shortcut it may never have had.
    /// </summary>
    private async Task<IReadOnlyList<ArchivePair>> SearchPassageLeadsAsync(
        float[]? query, IReadOnlyList<ArchivePair> index, CancellationToken cancellationToken)
    {
        // Recall depth, not PassageOptions.TopK: one slider governs every
        // cosine cut a turn makes, whether the corpus is archive rows or the
        // persona's own notes.
        var topK = _knobs.RecallDepth;
        if (query is null || topK <= 0)
        {
            return [];
        }

        var hits = await _passages.SearchAsync(query, topK, _passageOptions.MinScore, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        // A passage names the pairs that existed when it was written, and the
        // index is the only authority on what exists now — deleting a pair's
        // last row deletes its file, which is how a pair leaves the index. So
        // pointers are resolved against the live index rather than trusted,
        // and a stale one drops silently instead of sending Recall to read
        // nothing. A note may also name no pair at all, which costs nothing:
        // its text still reaches Intent through Hindsight.
        var known = index.ToDictionary(p => $"{p.Category}/{p.Topic}", StringComparer.OrdinalIgnoreCase);
        var pairs = hits
            .SelectMany(h => h.Passage.Pairs)
            .Select(p => known.GetValueOrDefault($"{p.Category}/{p.Topic}"))
            .OfType<ArchivePair>()
            .Distinct()
            .Take(Math.Min(_passageOptions.MaxPairsFromPassages, _knobs.VectorLanePairs))
            .ToList();

        _logger.LogInformation("{Agent} matched {Count} note(s) for {Pairs} lead(s)", Name, hits.Count, pairs.Count);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} passage hits: {Hits}", Name,
                string.Join(" | ", hits.Select(h => $"{h.Score:F3} {h.Passage.Text} -> [{string.Join(", ", h.Passage.Pairs.Select(p => $"{p.Category}/{p.Topic}"))}]")));
        }

        return pairs;
    }


    /// <summary>
    /// This turn as a vector, or null when there is nothing to embed or
    /// nothing to embed with. Null is a normal answer, not a failure: the
    /// offline tier has no embedder at all and every caller below is written
    /// to carry on without one.
    /// </summary>
    private async Task<float[]?> EmbedQueryAsync(string? text, CancellationToken cancellationToken)
    {
        if (!_embeddings.Available || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var vectors = await _embeddings.EmbedAsync([text], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            return vectors.Count == 0 ? null : vectors[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Agent} could not embed the turn; continuing without vector leads", Name);
            return null;
        }
    }

    /// <summary>Selection first, passage leads after, no duplicates — the LLM saw the whole index, a passage saw one past turn.</summary>
    /// <summary>
    /// What is left of the vector lane's budget after the passage leads have
    /// taken their share. One lane, two sources: a note saying "read X" and a
    /// gloss that matches the question are leads of the same standing, and
    /// the thread count promises a number of recalls, not a number of ways
    /// of arriving at one.
    /// </summary>
    private int VectorLaneRoom(IReadOnlyList<ArchivePair> remembered) =>
        Math.Max(0, _knobs.VectorLanePairs - remembered.Count);

    private static IReadOnlyList<ArchivePair> Merge(IReadOnlyList<ArchivePair> selected, IReadOnlyList<ArchivePair> remembered) =>
        [.. selected, .. remembered.Where(p => !selected.Contains(p))];

    /// <summary>
    /// Every pair except the "other" ones. Each category has an "other" topic
    /// on the write side — Cataloger's pressure valve for a fact no folder
    /// fits — and offering those here was measurably bad: shown as an option,
    /// "other" reads as "maybe" and gets picked over the topic that actually
    /// holds the answer. Same filing code, 71% of facts retrievable with them
    /// visible against 82% with them hidden. Hidden, they are still read: see
    /// WithOverflow.
    /// </summary>
    private static IReadOnlyList<ArchivePair> Shown(IReadOnlyList<ArchivePair> index) =>
        [.. index.Where(p => !string.Equals(p.Topic, OtherTopic, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Opens "category/other" whenever that category is opened. A fact ends
    /// up there because no folder fit it, not because it is unimportant — so
    /// the decision to read it is made in code from the category the selector
    /// already chose, rather than asked of a model that has only a folder name
    /// to go on. Appended after the selection, so it never displaces a pair
    /// the selector named within the selector lane's budget.
    /// </summary>
    private static IReadOnlyList<ArchivePair> WithOverflow(IReadOnlyList<ArchivePair> selected, IReadOnlyList<ArchivePair> index)
    {
        var overflow = selected
            .Select(p => new ArchivePair(p.Category, OtherTopic))
            .Distinct()
            .Where(p => index.Contains(p) && !selected.Contains(p));

        return [.. selected, .. overflow];
    }

    /// <summary>Mirrors ClosedVocabulary.OtherTopic — the read side is not allowed to depend on the write side's assembly.</summary>
    private const string OtherTopic = "other";

    /// <summary>
    /// The gloss lives in cataloger.txt beside the vocabulary it defines, and
    /// is read from there rather than copied here on purpose: the point of it
    /// is that the writer and the reader share one definition of what a folder
    /// holds, and two copies of a dictionary are two dictionaries. This is a
    /// read of another agent's instruction section, not a dependency on its
    /// code -- the rule this class keeps is that the read side does not bind
    /// to the write side's types, and OtherTopic is still mirrored rather
    /// than imported.
    /// </summary>
    private const string GlossOwner = "Cataloger";
    private const string GlossSection = "gloss";

    private string BuildSelectionPrompt(string? text, IReadOnlyList<ArchivePair> index)
    {
        var gloss = _gloss.Value;
        var options = string.Join("\n", index.Select((t, i) =>
        {
            var words = gloss?.For(t.Category, t.Topic);
            return words is null
                ? $"{i}. {t.Category}/{t.Topic}"
                : $"{i}. {t.Category}/{t.Topic} ({words})";
        }));
        return InstructionFile.Fill(_instructions.For(Name),
            ("options", options),
            ("max", _knobs.SelectorLanePairs.ToString()),
            ("text", text ?? string.Empty));
    }

    /// <summary>
    /// Indices the selector named, in the order it named them, each pair at
    /// most once and no more than the configured cap.
    ///
    /// Both limits are enforced here because the prompt cannot enforce
    /// either. "3, 3, 3" is a plausible thing for a small model to answer
    /// and would otherwise open one file three times and weight its rows
    /// three times in Intent's prompt; "0,1,2,...,40" would fan phase one
    /// out over 41 concurrent reads, which MaxConcurrentRecalls does not
    /// cover — that caps the chunks in phase two, after every selected pair
    /// has already been read.
    /// </summary>
    private IReadOnlyList<ArchivePair> ParsePairs(string response, IReadOnlyList<ArchivePair> index)
    {
        var selected = new List<ArchivePair>();
        var seen = new HashSet<int>();
        foreach (var i in InstructionFile.Indices(response))
        {
            if (selected.Count >= _knobs.SelectorLanePairs)
            {
                break;
            }

            if (i >= 0 && i < index.Count && seen.Add(i))
            {
                selected.Add(index[i]);
            }
        }

        return selected;
    }
}
