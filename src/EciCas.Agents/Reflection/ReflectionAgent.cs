using System.Diagnostics;
using System.Text.Json;
using EciCas.Agents.Archivist;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Reflection;

/// <summary>
/// Buffers concluded turns (same _pending shape as ArchivistAgent) and,
/// once ReflectionOptions.BatchSize accumulates, makes one substrate call
/// scoring candidate follow-up ideas. The best-ranked candidate is pushed
/// back onto events.perception — downstream nothing knows the difference
/// from external input — only when persona drive-vector state
/// (read from IArchiveStore at ImpulseAgent.DrivePath, never a direct
/// reference to ImpulseAgent) reads eager enough; otherwise it's archived
/// quietly like every other candidate. See roadmap.md's "Reflection Agent
/// redesign (drive-gated, batched)".
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: one substrate call per BATCH, not per envelope,
/// doesn't fit that base class's model — same rationale ArchivistAgent's
/// own doc comment gives for its choice.
///
/// Generation still guards against an idea -> arc -> conclusion -> idea loop
/// spending forever on LLM calls: a batch whose max generation is already at
/// MaxIdeaGeneration is scored (so nothing is lost) but never pushed.
/// </summary>
public sealed class ReflectionAgent : AgentBase, ICognitiveAgent
{
    public const string TriggeredByKey = "perception.triggered_by";
    public const string ReflectedKind = "Reflected";

    /// <summary>
    /// Slow-coloring feedback (Python current-spec.md §5.3), attached to the
    /// Reflected control envelope Reflection already publishes. Carries a
    /// mood LABEL, never a numeric delta: Impulse owns every number that
    /// lands on the drive vectors, the same discipline CriticalNudge and
    /// FrustrationNudge follow. Reflection is the right agent for this
    /// because it already reasons across a whole batch of concluded turns —
    /// Archivist stays a dumb per-turn fact writer.
    /// </summary>
    public const string MoodKey = "reflection.mood";

    // "assistant" rather than "self": the persona owns both the facts it
    // knows about itself and the ideas it has about them, and one category
    // for both means the pair label reads as an address rather than as a
    // mood. The pair is still its own file — the archive is pair-addressed,
    // so assistant~reflection.parquet never touches assistant~identity.parquet.
    private const string FixedCategory = "assistant";
    private const string FixedTopic = "reflection";
    private const string FixedSubject = "self";
    private const string FixedKey = "insight";
    private const double QuietImportance = 0.1;
    private const double PushedImportance = 0.2;

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly IPassageStore _passages;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IAgentStateStore _stateStore;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ILogger _logger;
    private readonly ReflectionOptions _options;
    private readonly List<BufferedConclusion> _pending = [];
    private readonly object _pendingLock = new();

    public ReflectionAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ReflectionAgent> logger, IArchiveStore store, IAgentStateStore stateStore,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ReflectionOptions> options,
        IPassageStore passages, IEmbeddingProvider embeddings, IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _passages = passages;
        _embeddings = embeddings;
        _stateStore = stateStore;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _logger = logger;
        _options = options.Value;
    }

    public override string Name => "Reflection";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Conclusion];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // What this turn gave Intent to work with: the reply-to text,
        // Impulse/Identity advice, Recall's picked facts with their full
        // path, and Hindsight's woken notes. Not the standing rules, which
        // are the same every turn and would otherwise fill this whole
        // budget. Governance forwards it unchanged from Intent's Proposal
        // through Verdict/Action/Conclusion (see IntentAgent.ContextKey) so
        // Reflection can weigh a reply against what it actually had.
        var context = envelope.Meta.Get<string>(IntentAgent.ContextKey) ?? string.Empty;
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;

        // Lineage, forwarded by Intent and Governance across the two hops
        // where Derive starts a fresh bag. Which notes were awake when this
        // turn was answered is what makes the new note's ancestry knowable.
        var wokenIds = envelope.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NoteIdsKey) ?? [];
        var wokenDepth = envelope.Meta.Get<int>(HindsightAgent.EchoDepthKey);

        List<BufferedConclusion>? batch = null;
        lock (_pendingLock)
        {
            _pending.Add(new BufferedConclusion(context, reply, envelope.Generation, wokenIds, wokenDepth));
            if (_pending.Count >= _options.BatchSize)
            {
                batch = [.. _pending];
                _pending.Clear();
            }
        }

        if (batch is null)
        {
            return;
        }

        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<BufferedConclusion> batch, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        // A deterministic-by-configuration Reflection has no idea to
        // propose: it drops the batch on purpose, which is not a failure and
        // so does not get retried.
        if (!entry.UseSubstrate)
        {
            return;
        }

        var previous = await SelectRevisitAsync(batch, cancellationToken).ConfigureAwait(false);
        // One read of the drive history serves both jobs below: the note is
        // written knowing how the mood has been moving, and the same newest
        // state decides whether the idea is worth pushing.
        var (eagerness, driveTrend) = await GetDriveAsync(cancellationToken).ConfigureAwait(false);

        List<Candidate> candidates;
        string? mood;
        List<Note> notes;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var batchPrompt = BuildBatchPrompt(batch, previous, driveTrend);
            _logger.LogDebug("{Agent} prompt >>>\n{Prompt}", Name, batchPrompt);
            var result = await _substrate.CompleteAsync(entry.Class, batchPrompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call [{Class}]: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, entry.Class, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);
            _logger.LogDebug("{Agent} response <<<\n{Response}", Name, result.Text);

            // Same guard as ArchivistAgent.ExtractFactsAsync: the mock
            // tier echoes the prompt back verbatim, and its own worked
            // example ("0.7|hypothesis|...") is a valid score|subtopic|idea
            // line that ParseCandidates would otherwise harvest as real.
            if (result.Text.Contains(batchPrompt, StringComparison.Ordinal))
            {
                _logger.LogInformation("{Agent} substrate echoed the prompt, nothing extracted", Name);
                return;
            }

            candidates = ParseCandidates(result.Text);
            mood = ParseMood(result.Text);
            notes = ParseNotes(result.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Closed fallback posture: a broken substrate call publishes no
            // idea, rather than guessing at one. But the raw material goes
            // back in the buffer instead of being thrown away — an outage
            // should cost the persona a delay in having its own thoughts,
            // not the turns it would have had them about. Capped at
            // MaxBufferedBatches so a long outage can't grow the buffer
            // without limit, or hand the substrate an enormous prompt the
            // moment it comes back.
            _logger.LogWarning("{Agent} batch scoring {Cause} after {LatencyMs}ms, retaining {Count} turns for the next flush",
                Name, SubstrateHealth.Classify(ex), Stopwatch.GetElapsedTime(started).TotalMilliseconds, batch.Count);

            lock (_pendingLock)
            {
                _pending.InsertRange(0, batch);
                var cap = _options.BatchSize * Math.Max(1, _options.MaxBufferedBatches);
                if (_pending.Count > cap)
                {
                    _pending.RemoveRange(0, _pending.Count - cap);
                }
            }

            return;
        }

        await WritePassagesAsync(notes, previous, batch, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            // A batch can legitimately surface no idea and still have a
            // tone worth colouring by, so the control envelope goes out
            // either way — only a failed or echoed call skips it.
            PublishReflected(mood);
            return;
        }

        var best = candidates.MaxBy(c => c.Score)!;
        var maxGeneration = batch.Max(b => b.Generation);
        var shouldPush = maxGeneration < _options.MaxIdeaGeneration && eagerness >= _options.EagernessThreshold;

        var now = DateTimeOffset.UtcNow;
        // Category/Topic are fixed (assistant/reflection); Subtopic is the LLM's
        // own free-text functional label per candidate — not constrained to
        // an enumerated list, so nuance isn't lost to a fixed vocabulary.
        // Importance is fixed by role (quiet vs. pushed), not LLM-scored —
        // candidate.Score stays purely for picking `best` among the batch.
        var internalRecords = candidates
            .Select(candidate => new ArchiveRecord(
                FixedCategory, FixedTopic, candidate.Subtopic, FixedSubject, FixedKey, candidate.Idea,
                now, ArchiveDomain.Internal, candidate == best && shouldPush ? PushedImportance : QuietImportance))
            .ToList();
        // profileId null: Reflection's own ideas belong to the persona, not
        // to whoever happened to be talking when it had them.
        await _store.WriteAsync(internalRecords, profileId: null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Agent} wrote {Count} record(s): {Paths}", Name, internalRecords.Count,
            string.Join(", ", internalRecords.Select(r => $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value} (importance {r.Importance})")));

        if (shouldPush)
        {
            _logger.LogInformation("{Agent} pushed idea: {Idea}", Name, best.Idea);
            var idea = Envelope.Create(Topics.Perception, Name, Severity.Restful,
                MetaBag.Empty.With(PerceptionAgent.TextKey, best.Idea).With(TriggeredByKey, "self"),
                generation: maxGeneration + 1);
            _bus.Publish(Topics.Perception, idea);
        }

        PublishReflected(mood);
    }

    /// <summary>
    /// One control envelope carries both the write-epoch signal and the
    /// batch's mood label — no new message type, and Impulse is already
    /// subscribed to system.control for GovernanceAgent.FrustrationKind.
    /// </summary>
    private void PublishReflected(string? mood)
    {
        var meta = MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ReflectedKind);
        if (mood is not null)
        {
            meta = meta.With(MoodKey, mood);
        }

        _bus.Publish(Topics.SystemControl, Envelope.Create(Topics.SystemControl, Name, Severity.Neutral, meta));
    }

    /// <summary>
    /// Reads the retained window of drive states in one pass and returns
    /// both things Reflection wants from it: how eager the persona is right
    /// now, and how it has been moving.
    ///
    /// One lookup, because these were nearly two — the gate has always read
    /// the newest state, and the trend needs that same newest state plus the
    /// ones behind it. Asking twice would scan the file twice for a strict
    /// superset of the same lines.
    /// </summary>
    private async Task<(double Eagerness, string Trend)> GetDriveAsync(CancellationToken cancellationToken)
    {
        var records = await _stateStore.LookupAsync(
            [ImpulseAgent.DrivePath], maxPerPath: _options.DriveHistory, cancellationToken).ConfigureAwait(false);

        // Newest first, matching the store's reverse scan — DriveTrend reads
        // index 0 as now.
        var states = records
            .Select(r => TryDeserialize(r.Content))
            .OfType<DriveVectors>()
            .ToList();

        var now = states.Count > 0 ? states[0] : new DriveVectors();

        // Ports Python's `engagement` appraisal axis (curiosity - 0.4*fatigue)
        // from agents/impulse/agent.py — the closest existing analog to "eager
        // enough to share an idea"; no new formula invented.
        return (Math.Clamp(now.Curiosity - 0.4 * now.Fatigue, 0.0, 1.0), DriveTrend.Describe(states));
    }

    /// <summary>
    /// A drive line this build cannot read is skipped, not fatal. The store
    /// keeps lines it could not parse on purpose, and a persona that refuses
    /// to reflect because one old state has a field it doesn't recognise
    /// would be trading the whole faculty for a schema change.
    /// </summary>
    private static DriveVectors? TryDeserialize(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<DriveVectors>(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The thought note is told it has no reader, deliberately. Reflection
    /// already has an advice channel — the scored candidates, one of which
    /// is pushed onto events.perception under drive and generation gates.
    /// Naming Intent as an audience for the note as well would turn an
    /// observation into a second piece of advice, written to land rather
    /// than to be true, and close the ring Hindsight -> Intent -> reply ->
    /// note -> Hindsight with nothing outside it. Continuity does not need
    /// an audience: "it joins the ones you wrote before it" is what buys
    /// the trail. Hindsight decides later whether a note is worth
    /// surfacing; the writer does not get a vote.
    /// </summary>
    /// <summary>
    /// Which stored thought this batch may sharpen. Similarity, not recency.
    ///
    /// Taking the newest note makes the corpus a chain: a thought is open to
    /// revision for exactly one batch and is then frozen for good, however
    /// often the persona thinks near it again. Taking the nearest one makes
    /// it a trail — a note from months ago becomes reachable the day the
    /// persona circles back to what it was about, which is the whole reason
    /// these are vectors and not a log.
    ///
    /// The query is the batch's own replies, so this costs one embed per
    /// batch and no substrate call at all. With no embedder, or nothing
    /// clearing RevisitMinScore, it falls back to the newest note: "the
    /// weights aren't downloaded" must keep meaning "the pre-vector
    /// behaviour", never "no revisits at all".
    /// </summary>
    private async Task<Passage?> SelectRevisitAsync(List<BufferedConclusion> batch, CancellationToken cancellationToken)
    {
        if (_embeddings.Available)
        {
            var query = PromptCap.Apply(string.Join(" ", batch.Select(b => b.ReplyText)));
            var vectors = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
            if (vectors.Count > 0)
            {
                var hits = await _passages
                    .SearchAsync(vectors[0], topK: 1, _options.RevisitMinScore, cancellationToken)
                    .ConfigureAwait(false);
                if (hits.Count > 0)
                {
                    return hits[0].Passage;
                }
            }
        }

        return await _passages.LatestAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildBatchPrompt(List<BufferedConclusion> batch, Passage? previous, string driveTrend)
    {
        var turns = string.Join("\n\n", batch.Select((b, i) =>
            $"{i + 1}. Given: {PromptCap.Apply(b.Context)}\n   Replied: {PromptCap.Apply(b.ReplyText)}"));

        // The previous batch's note goes back in so its rewrite is a second
        // reading of the same event-series with hindsight, not a fresh guess
        // at what it meant. Absent on the very first batch, and its line is
        // simply not asked for then.
        var revisit = previous is null
            ? string.Empty
            : Environment.NewLine + InstructionFile.Fill(_instructions.For(Name, "revisit"),
                ("previous", PromptCap.Apply(previous.Text)),
                ("topics", string.Join(", ", previous.Pairs.Select(p => $"{p.Category}/{p.Topic}"))));

        return InstructionFile.Fill(_instructions.For(Name),
            ("terse", ArchiveWriteStyle.TerseValue),
            ("moods", MoodLabels),
            ("drive", driveTrend),
            ("revisit", revisit),
            ("turns", turns));
    }

    /// <summary>
    /// The closed vocabulary Reflection may report. Closed on purpose: an
    /// open-ended label would make ImpulseAgent.SlowColoring's lookup miss
    /// silently, and a mood no one mapped is indistinguishable from no mood
    /// at all. Impulse ignores anything not in its own table regardless.
    /// </summary>
    private static readonly string[] Moods = ["warm", "tense", "dull", "curious", "neutral"];

    private static string MoodLabels => string.Join(", ", Moods);

    /// <summary>
    /// The mood line is parsed separately from candidates rather than as a
    /// fourth candidate field: it describes the batch as a whole, not any one
    /// idea, and it must survive a batch that produced no candidates at all.
    /// </summary>
    private static string? ParseMood(string response)
    {
        foreach (var line in response.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length == 2 && parts[0].Trim().Equals("mood", StringComparison.OrdinalIgnoreCase))
            {
                var label = parts[1].Trim().ToLowerInvariant();
                if (Moods.Contains(label))
                {
                    return label;
                }
            }
        }

        return null;
    }

    private static List<Candidate> ParseCandidates(string response)
    {
        var candidates = new List<Candidate>();
        foreach (var line in response.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length != 3)
            {
                continue;
            }

            var scoreText = parts[0].Trim();
            var subtopic = parts[1].Trim();
            var idea = parts[2].Trim();
            if (idea.Length > 0 && subtopic.Length > 0 && double.TryParse(scoreText, out var score))
            {
                candidates.Add(new Candidate(Math.Clamp(score, 0.0, 1.0), subtopic, idea));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Embeds the batch's notes and lands them in the passage corpus: the
    /// current-batch note as a new row, the revisit under the previous row's
    /// id and timestamp so it replaces that note rather than accumulating
    /// beside it. Keeping the old timestamp is what keeps "latest" meaning
    /// the most recent event-series rather than the most recent edit.
    ///
    /// Both texts go through one EmbedAsync call. Failure here loses the
    /// notes and nothing else — the batch's ideas, mood and archive writes
    /// have already happened, and a corpus that missed one entry is a weaker
    /// shortcut, not a broken turn.
    /// </summary>
    private async Task WritePassagesAsync(List<Note> notes, Passage? previous, List<BufferedConclusion> batch, CancellationToken cancellationToken)
    {
        if (notes.Count == 0 || !_embeddings.Available)
        {
            return;
        }

        // A revisit with nothing to revise is the model answering a question
        // it wasn't asked; without a previous row there is no id to write it
        // under.
        var revisit = previous is null ? null : notes.FirstOrDefault(n => n.IsRevisit);
        var current = notes.FirstOrDefault(n => !n.IsRevisit);
        var writing = new[] { revisit, current }.OfType<Note>().ToList();
        if (writing.Count == 0)
        {
            return;
        }

        var vectors = await _embeddings.EmbedAsync([.. writing.Select(n => n.Text)], cancellationToken).ConfigureAwait(false);
        if (vectors.Count != writing.Count)
        {
            _logger.LogWarning("{Agent} could not embed its notes, passage corpus unchanged", Name);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Every note Hindsight woke across the batch is a parent of what the
        // batch produced: those thoughts were in the prompts these replies
        // were written from. Depth is one past the deepest of them, so a
        // thought traceable to turns the persona had not already coloured
        // sits at zero and a resonance climbs.
        var parents = batch.SelectMany(b => b.WokenNoteIds).Distinct().ToList();
        var depth = parents.Count == 0 ? 0 : batch.Max(b => b.WokenEchoDepth) + 1;
        var generation = batch.Max(b => b.Generation);

        var added = writing
            .Select((n, i) => n.IsRevisit
                // A revisit is the same thought sharpened, so it keeps its
                // own ancestry along with its id and timestamp. Revising a
                // thought does not make it a descendant of itself.
                ? previous! with { Text = n.Text, Pairs = n.Pairs, Embedding = vectors[i], ModelId = _embeddings.ModelId }
                : new Passage(Guid.NewGuid().ToString("n"), n.Text, n.Pairs, now, vectors[i], parents, depth, generation, _embeddings.ModelId))
            .ToList();

        await _passages.WriteAsync(added, revisit is null ? null : previous!.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Agent} wrote {Count} passage(s): {Texts}", Name, added.Count,
            string.Join(" | ", added.Select(p => $"[{p.Id}] {p.Text} -> [{string.Join(", ", p.Pairs.Select(q => $"{q.Category}/{q.Topic}"))}]")));
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{Agent} passage detail: supersedes {Superseded}, parents [{Parents}], echo depth {Depth}, generation {Generation}, model {Model}",
                Name, revisit is null ? "nothing" : previous!.Id, string.Join(", ", parents), depth, generation, _embeddings.ModelId);
        }
    }

    /// <summary>
    /// "thought|pairs|note" and "revisit|pairs|note". Parsed apart from the
    /// candidate lines rather than as a fourth candidate field: a note
    /// describes the whole batch, not any one idea, and it has to survive a
    /// batch that proposed no ideas at all.
    ///
    /// Was "missed|..." — a critique of what retrieval failed to fetch.
    /// That made the corpus a retrieval-tuning log; the pairs it named were
    /// its only content. The note is now the persona's own reading of a
    /// stretch of turns, which is what Hindsight is meant to surface later.
    /// The pairs field survives the change on purpose: it is the one thing
    /// about a note that reality can contradict, and a note that cannot be
    /// wrong is a note that compounds unchecked.
    /// </summary>
    private static List<Note> ParseNotes(string response)
    {
        var notes = new List<Note>();
        foreach (var line in response.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length != 3)
            {
                continue;
            }

            var kind = parts[0].Trim().ToLowerInvariant();
            if (kind is not ("thought" or "revisit"))
            {
                continue;
            }

            var text = parts[2].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            notes.Add(new Note(kind == "revisit", text, ParsePairs(parts[1])));
        }

        return notes;
    }

    /// <summary>
    /// Pairs are stored lowercase because the archive treats an address as
    /// case-insensitive; Librarian resolves them against the live index
    /// anyway, so anything malformed here costs a dropped pointer, never a
    /// bad read.
    /// </summary>
    private static IReadOnlyList<ArchivePair> ParsePairs(string field) =>
        [.. field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split('/', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0)
            .Select(p => new ArchivePair(p[0].Trim().ToLowerInvariant(), p[1].Trim().ToLowerInvariant()))
            .Distinct()];

    private sealed record BufferedConclusion(
        string Context, string ReplyText, int Generation, IReadOnlyList<string> WokenNoteIds, int WokenEchoDepth);

    private sealed record Candidate(double Score, string Subtopic, string Idea);
    private sealed record Note(bool IsRevisit, string Text, IReadOnlyList<ArchivePair> Pairs);
}
