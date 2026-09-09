using System.Diagnostics;
using EciCas.Agents.Archivist;
using EciCas.Agents.Identity;
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
    private readonly SubstrateOptions _substrates;
    private readonly CatalogerOptions _options;
    private readonly ILogger _logger;
    private readonly Lazy<ClosedVocabulary> _vocabulary;

    // Pending facts carry the profile that stated them: a batch can span
    // turns, and by the time it flushes the speaker is long gone from scope.
    private readonly List<(string? ProfileId, ArchiveRecord Record)> _pending = [];
    private readonly object _pendingLock = new();
    private int _turnsSinceFlush;

    public CatalogerAgent(IMessageBus bus, BusActivityTracker activity, ILogger<CatalogerAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<SubstrateOptions> substrates, IOptions<CatalogerOptions> options,
        IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _substrate = substrate;
        _substrates = substrates.Value;
        _options = options.Value;
        _logger = logger;
        _vocabulary = new Lazy<ClosedVocabulary>(() => ClosedVocabulary.Parse(_instructions.For(Name, VocabularySection)));
    }

    public override string Name => "Cataloger";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Facts];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_substrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No substrate entry for agent '{Name}' — add one to appsettings.json's Substrates:Agents section.");
        }

        var facts = envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(ArchivistAgent.FactsKey) ?? [];
        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        // Reserved addresses first, and outside the substrate switch on
        // purpose. These are decided by who the fact is about rather than by
        // ranking it against a vocabulary, so there is no call to make and
        // nothing for a deterministic Cataloger to skip -- a rename works on
        // a tier with no filing model at all.
        var reserved = new List<ArchiveRecord>();
        var open = new List<ArchiveRecord>();
        foreach (var fact in facts)
        {
            if (PersonaName.Rename(fact) is { } renamed)
            {
                reserved.Add(renamed);
            }
            else
            {
                open.Add(fact);
            }
        }

        // A deterministic-by-configuration Cataloger files nothing else.
        // There is no keyword fallback and there should not be one: guessing
        // an address is exactly the behaviour this agent exists to remove.
        IReadOnlyList<ArchiveRecord> filed = entry.UseSubstrate
            ? [.. reserved, .. await FileAsync(envelope, open, text, cancellationToken).ConfigureAwait(false)]
            : reserved;

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
        string text, CancellationToken cancellationToken)
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

            // The persona's shelf is chosen by who the fact is about, not by
            // ranking it against a vocabulary that describes the user's
            // domain. One call instead of two, and `assistant` never has to
            // win a similarity contest against `household` — measured 1 of
            // 16 the other way (tools/retrieval-bench/refl_v4.py).
            //
            // The drawer inside it is still picked by the model, from a
            // closed list, by the same prompt every other fact uses. Nothing
            // was added to an instruction file for this: the subject field
            // Archivist already fills is the whole routing rule.
            var self = AssistantScope.IsSelf(fact.Subject);
            string category;

            if (self)
            {
                category = AssistantScope.Name;
            }
            else
            {
                var answer = await AskAsync(envelope,
                    InstructionFile.Fill(_instructions.For(Name, CategorySection), ("text", text), ("fact", line)),
                    cancellationToken).ConfigureAwait(false);

                if (answer is not null && _vocabulary.Value.MatchCategory(answer) is { } named)
                {
                    category = named;
                }
                else
                {
                    // Not a drop. A stated fact with nowhere obvious to go is
                    // still a stated fact, and the topic call has never been
                    // allowed to lose one — see ClosedVocabulary.Unfiled for
                    // why the fallback is a visible pair rather than an
                    // "other". Still logged loudly: a run of these means the
                    // vocabulary is missing something real.
                    _logger.LogWarning("{Agent} found no category for '{Fact}' (answer: {Reply}) — filed at {Pair}",
                        Name, line, answer ?? "call failed", ClosedVocabulary.Unfiled);
                    addressed.Add(fact with
                    {
                        Category = ClosedVocabulary.Unfiled.Category,
                        Topic = ClosedVocabulary.Unfiled.Topic,
                    });
                    continue;
                }
            }

            // "other" ends every list, here as in the vocabulary file: the
            // valve is what keeps a fact that fits no folder from being
            // forced into a loose one.
            IReadOnlyList<string> topics = self
                ? [.. AssistantScope.Topics, ClosedVocabulary.OtherTopic]
                : _vocabulary.Value.TopicsIn(category);

            var reply = await AskAsync(envelope,
                InstructionFile.Fill(_instructions.For(Name, TopicSection),
                    ("cat", category), ("topics", string.Join("  ", topics)), ("text", text), ("fact", line)),
                cancellationToken).ConfigureAwait(false);

            // A failed topic call still has a drawer, and "other" is a real
            // folder in every drawer — better than losing the fact over the
            // cheaper of the two decisions.
            var topic = reply is null ? ClosedVocabulary.OtherTopic : ClosedVocabulary.MatchTopic(topics, reply);

            addressed.Add(fact with { Category = category, Topic = topic });
        }

        return addressed;
    }

    /// <summary>
    /// One call, or null if it failed. Errors are logged and swallowed — same
    /// posture as FallbackPosture.Closed — because this whole path runs behind
    /// the reply and a filing failure must never surface as a turn failure.
    /// </summary>
    private async Task<string?> AskAsync(Envelope envelope, string prompt, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            _logger.LogDebug("{Agent} prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(Name, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} response <<<\n{Response}", Name, result.Text);
            SubstrateTrace.Publish(_bus, envelope, Name, result);

            // The mock tier echoes its prompt back, and a reply containing its
            // own entire input names no drawer.
            return result.Text.Contains(prompt, StringComparison.Ordinal) ? null : result.Text;
        }
        catch (Exception ex) when (!SubstrateHealth.IsShutdown(ex, cancellationToken))
        {
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} filing call {Cause}, skipping", Name, cause);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, cause);
            return null;
        }
    }
}
