using System.Diagnostics;
using System.Text.Json;
using EciCas.Agents.Archivist;
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
/// from external input (plan §3.6) — only when persona drive-vector state
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
    public const string SourceTypeKey = "perception.source_type";
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

    private const string FixedCategory = "self";
    private const string FixedTopic = "reflection";
    private const string FixedSubject = "self";
    private const string FixedKey = "insight";
    private const double QuietImportance = 0.1;
    private const double PushedImportance = 0.2;

    private readonly IMessageBus _bus;
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
        IPassageStore passages, IEmbeddingProvider embeddings)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
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
        // Same prompt Intent itself was given (reply-to text, Impulse/Identity
        // advice, Recall's picked facts with their full category/topic/
        // subtopic path) plus what it sent out — Governance forwards both
        // unchanged from Intent's own Proposal through Verdict/Action/
        // Conclusion (see IntentAgent.PromptKey) so Reflection can weigh a
        // reply against exactly what it had access to, not just the text.
        var prompt = envelope.Meta.Get<string>(IntentAgent.PromptKey) ?? string.Empty;
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;

        List<BufferedConclusion>? batch = null;
        lock (_pendingLock)
        {
            _pending.Add(new BufferedConclusion(prompt, reply, envelope.Generation));
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

        var previous = await _passages.LatestAsync(cancellationToken).ConfigureAwait(false);

        List<Candidate> candidates;
        string? mood;
        List<Note> notes;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var batchPrompt = BuildBatchPrompt(batch, previous);
            var result = await _substrate.CompleteAsync(entry.Class, batchPrompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Agent} substrate call: {LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost",
                Name, result.Latency.TotalMilliseconds, result.TokenCount, result.Cost);

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

        await WritePassagesAsync(notes, previous, cancellationToken).ConfigureAwait(false);

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
        var eagerness = await GetEagernessAsync(cancellationToken).ConfigureAwait(false);
        var shouldPush = maxGeneration < _options.MaxIdeaGeneration && eagerness >= _options.EagernessThreshold;

        var now = DateTimeOffset.UtcNow;
        // Category/Topic are fixed (self/reflection); Subtopic is the LLM's
        // own free-text functional label per candidate — not constrained to
        // an enumerated list, so nuance isn't lost to a fixed vocabulary.
        // Importance is fixed by role (quiet vs. pushed), not LLM-scored —
        // candidate.Score stays purely for picking `best` among the batch.
        var internalRecords = candidates
            .Select(candidate => new ArchiveRecord(
                FixedCategory, FixedTopic, candidate.Subtopic, FixedSubject, FixedKey, PromptCap.Apply(candidate.Idea),
                now, ArchiveDomain.Internal, candidate == best && shouldPush ? PushedImportance : QuietImportance))
            .ToList();
        // profileId null: Reflection's own ideas belong to the persona, not
        // to whoever happened to be talking when it had them.
        await _store.WriteAsync(internalRecords, profileId: null, cancellationToken).ConfigureAwait(false);

        if (shouldPush)
        {
            _logger.LogInformation("{Agent} pushed idea: {Idea}", Name, best.Idea);
            var idea = Envelope.Create(Topics.Perception, Name, Severity.Restful,
                MetaBag.Empty.With(PerceptionAgent.TextKey, best.Idea).With(TriggeredByKey, "self").With(SourceTypeKey, "idea"),
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

    private async Task<double> GetEagernessAsync(CancellationToken cancellationToken)
    {
        var records = await _stateStore.LookupAsync([ImpulseAgent.DrivePath], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
        var vectors = records.Count > 0
            ? JsonSerializer.Deserialize<DriveVectors>(records[0].Content) ?? new DriveVectors()
            : new DriveVectors();

        // Ports Python's `engagement` appraisal axis (curiosity - 0.4*fatigue)
        // from agents/impulse/agent.py — the closest existing analog to "eager
        // enough to share an idea"; no new formula invented.
        return Math.Clamp(vectors.Curiosity - 0.4 * vectors.Fatigue, 0.0, 1.0);
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
    private static string BuildBatchPrompt(List<BufferedConclusion> batch, Passage? previous)
    {
        var turns = string.Join("\n\n", batch.Select((b, i) =>
            $"{i + 1}. Given: {PromptCap.Apply(b.Prompt)}\n   Replied: {PromptCap.Apply(b.ReplyText)}"));

        // The previous batch's note goes back in so its rewrite is a second
        // reading of the same event-series with hindsight, not a fresh guess
        // at what it meant. Absent on the very first batch, and its line is
        // simply not asked for then.
        var revisit = previous is null
            ? string.Empty
            : $"""

            You wrote this note after the previous batch:
            "{PromptCap.Apply(previous.Text)}" (topics: {string.Join(", ", previous.Pairs.Select(p => $"{p.Category}/{p.Topic}"))})
            Now that you have seen what followed, carry it forward as one
            "revisit|<category/topic, ...>|<note>" line in the same form.
            Keep it if it still holds, sharpen it if it has grown clearer,
            change it if these turns argue against it — and if it has been
            overtaken, say the newer thing rather than defending the old one.
            Revise it; do not restate it. Writing this line replaces the old
            note.
            """;

        return $"""
            From these recent interactions — what Intent was given (the turn,
            any advice, and recalled facts) and how it replied — propose
            follow-up thoughts or questions worth exploring later. Respond
            with zero or more lines, each formatted as "score|subtopic|idea"
            where score is 0.0-1.0 insight-worthiness and subtopic is a short
            (1-2 word) functional label for the kind of thought it is — pick
            whatever label fits best, e.g. pattern, hypothesis, meta-rule,
            synthesis, question, or another label of your own choosing. Idea
            ({ArchiveWriteStyle.TerseValue}), e.g.
            "0.7|hypothesis|trip dates vs deadline". If nothing stands out,
            respond with no candidate lines.

            On its own line, describe the overall tone of these interactions
            as "mood|<label>" where label is exactly one of: {MoodLabels}.
            Always include this line, even when you propose no ideas.

            Then write one line "thought|<category/topic, ...>|<note>" — not
            about what these turns said, but about what they made you notice.
            10-25 words. Read both halves: what Intent was given, and how it
            chose to reply. A habit in its phrasing, a tension between what
            was asked and what was answered, a connection the facts alone do
            not carry — places to look, not a list to fill. This note is
            yours and nobody wrote it for you, so let it be particular
            rather than agreeable — an opinion you would still hold if it
            were unwelcome. Write it for no one. It joins the ones you wrote
            before it, so follow where your own thinking has been going
            rather than starting over each time. The topics are the pairs
            the thought touches, so it can be checked against what actually
            exists: use only pairs appearing in the interactions above, and
            leave the field empty if it touches none.
            Write no line at all if nothing struck you.
            {revisit}

            {ArchiveWriteStyle.EnglishFields}

            Interactions:
            {turns}
            """;
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
    private async Task WritePassagesAsync(List<Note> notes, Passage? previous, CancellationToken cancellationToken)
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
        var added = writing
            .Select((n, i) => n.IsRevisit
                ? new Passage(previous!.Id, n.Text, n.Pairs, previous.Timestamp, vectors[i])
                : new Passage(Guid.NewGuid().ToString("n"), n.Text, n.Pairs, now, vectors[i]))
            .ToList();

        await _passages.WriteAsync(added, revisit is null ? null : previous!.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Agent} wrote {Count} passage(s): {Texts}", Name, added.Count, string.Join(" | ", added.Select(p => p.Text)));
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

            notes.Add(new Note(kind == "revisit", PromptCap.Apply(text), ParsePairs(parts[1])));
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

    private sealed record BufferedConclusion(string Prompt, string ReplyText, int Generation);

    private sealed record Candidate(double Score, string Subtopic, string Idea);
    private sealed record Note(bool IsRevisit, string Text, IReadOnlyList<ArchivePair> Pairs);
}
