using System.Diagnostics;
using EciCas.Agents.Archivist;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Cataloger;

/// <summary>
/// The other half of the Librarian's job, and deliberately not inside it:
/// Librarian reads, this writes. They face opposite directions, they fail in
/// opposite ways, and they can sit on different tiers — a wrong pick on the
/// read side costs one turn, a wrong pick here is on disk forever.
///
/// Takes the facts Archivist extracted, gives each one an address, and writes
/// it. Two substrate calls per fact: the drawer first, then the folders inside
/// that drawer, both picked from a closed list (see ClosedVocabulary). The
/// path itself is built here in code — no model ever emits a file name, which
/// is what "closed" actually buys.
///
/// Two calls rather than one because a small model shown both levels at once
/// answers with a plausible-sounding pair instead of a listed one. The cost is
/// paid off the critical path: this whole chain hangs behind events.bundle in
/// parallel with Intent, so the user waits for none of it.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: it makes a variable number of calls and batches
/// its results rather than publishing one parsed answer per turn — the same
/// rationale ArchivistAgent's own class remarks give.
/// </summary>
public sealed class CatalogerAgent : AgentBase, ICognitiveAgent
{
    private const string CategorySection = "category";
    private const string TopicSection = "topic";
    private const string VocabularySection = "vocabulary";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly IInstructionStore _instructions;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly CatalogerOptions _options;
    private readonly ILogger _logger;
    private readonly Lazy<ClosedVocabulary> _vocabulary;

    // Pending facts carry the profile that stated them: a batch can span
    // turns, and by the time it flushes the speaker is long gone from scope.
    private readonly List<(string? ProfileId, ArchiveRecord Record)> _pending = [];
    private readonly object _pendingLock = new();
    private int _turnsSinceFlush;

    public CatalogerAgent(IMessageBus bus, BusActivityTracker activity, ILogger<CatalogerAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<CatalogerOptions> options,
        IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _logger = logger;
        _vocabulary = new Lazy<ClosedVocabulary>(() => ClosedVocabulary.Parse(_instructions.For(Name, VocabularySection)));
    }

    public override string Name => "Cataloger";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Facts];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        var facts = envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(ArchivistAgent.FactsKey) ?? [];
        var text = PromptCap.Apply(envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty);

        // A deterministic-by-configuration Cataloger files nothing. There is
        // no keyword fallback and there should not be one: guessing an
        // address is exactly the behaviour this agent exists to remove.
        IReadOnlyList<ArchiveRecord> filed = entry.UseSubstrate
            ? await FileAsync(envelope, facts, text, entry.Class, cancellationToken).ConfigureAwait(false)
            : [];

        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        List<(string? ProfileId, ArchiveRecord Record)>? batch = null;
        lock (_pendingLock)
        {
            _pending.AddRange(filed.Select(r => (profileId, r)));
            _turnsSinceFlush++;
            if (_turnsSinceFlush >= _options.BatchSize && _pending.Count > 0)
            {
                batch = [.. _pending];
                _pending.Clear();
                _turnsSinceFlush = 0;
            }
        }

        if (batch is null)
        {
            return;
        }

        // One write per profile in the batch — the store decides per record
        // whether the fact lands in that profile's tier or the shared one.
        await Task.WhenAll(batch
            .GroupBy(p => p.ProfileId)
            .Select(g => _store.WriteAsync([.. g.Select(p => p.Record)], g.Key, cancellationToken)))
            .ConfigureAwait(false);

        var written = batch.Select(p => Describe(p.Record)).ToList();
        _logger.LogInformation("{Agent} wrote {Count} records: {Paths}", Name, batch.Count, string.Join(", ", written));

        // Still ArchivistAgent's constants: the announcement is about the
        // archive, and Identity/Impulse listen for that kind of event, not
        // for whichever agent happened to hold the pen this month.
        var announcement = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ArchivistAgent.WrittenKind)
                .With(ArchivistAgent.WrittenRecordsKey, written));
        _bus.Publish(Topics.SystemControl, announcement);
    }

    private static string Describe(ArchiveRecord r) =>
        $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}";

    private async Task<IReadOnlyList<ArchiveRecord>> FileAsync(Envelope envelope, IReadOnlyList<ArchiveRecord> facts,
        string text, string substrateClass, CancellationToken cancellationToken)
    {
        var addressed = new List<ArchiveRecord>();

        // Sequential, not fanned out: the local provider caps concurrency
        // anyway, and a turn rarely yields more than two or three facts.
        foreach (var fact in facts)
        {
            // The fact as one line, in the shape the archive stores it.
            // Handing the model the fields separately made it answer about
            // the field names.
            var line = $"{fact.Subtopic} {fact.Subject} {fact.Key} = {fact.Value}";

            var reply = await AskAsync(envelope, substrateClass,
                InstructionFile.Fill(_instructions.For(Name, CategorySection), ("text", text), ("fact", line)),
                cancellationToken).ConfigureAwait(false);

            if (reply is null || _vocabulary.Value.MatchCategory(reply) is not { } category)
            {
                // Nothing sensible to do with a fact that has no drawer: the
                // file name is the whole index in this store, so an invented
                // one is a file nobody ever opens. Logged loudly because a
                // run of these means the vocabulary is missing something real.
                _logger.LogWarning("{Agent} found no category for '{Fact}' (answer: {Reply}) — fact dropped",
                    Name, line, reply ?? "call failed");
                continue;
            }

            var options = string.Join("  ", _vocabulary.Value.TopicsIn(category));
            reply = await AskAsync(envelope, substrateClass,
                InstructionFile.Fill(_instructions.For(Name, TopicSection),
                    ("cat", category), ("topics", options), ("text", text), ("fact", line)),
                cancellationToken).ConfigureAwait(false);

            // A failed topic call still has a drawer, and "other" is a real
            // folder in every drawer — better than losing the fact over the
            // cheaper of the two decisions.
            var topic = reply is null ? ClosedVocabulary.OtherTopic : _vocabulary.Value.MatchTopic(category, reply);

            addressed.Add(fact with { Category = category, Topic = topic });
        }

        return addressed;
    }

    /// <summary>
    /// One call, or null if it failed. Errors are logged and swallowed — same
    /// posture as FallbackPosture.Closed — because this whole path runs behind
    /// the reply and a filing failure must never surface as a turn failure.
    /// </summary>
    private async Task<string?> AskAsync(Envelope envelope, string substrateClass, string prompt, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            _logger.LogDebug("{Agent} prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} response <<<\n{Response}", Name, result.Text);
            SubstrateTrace.Publish(_bus, envelope, Name, substrateClass, result);

            // The mock tier echoes its prompt back, and a reply containing its
            // own entire input names no drawer.
            return result.Text.Contains(prompt, StringComparison.Ordinal) ? null : result.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} filing call {Cause}, skipping", Name, cause);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, substrateClass, Stopwatch.GetElapsedTime(started).TotalMilliseconds, cause);
            return null;
        }
    }
}
