using System.Diagnostics;
using System.Text.RegularExpressions;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Archivist;

/// <summary>
/// Was Consolidator, which was accurate — memory consolidation is the
/// literal term for what it does — but named the process rather than the
/// role, and stood alone in a roster that now has a Librarian. Archivist
/// says the same thing and says it in the library's vocabulary: it decides
/// what is worth keeping and files it where Librarian can later find it.
///
/// Parallel publisher on events.bundle alongside Intent — never through the
/// live reply path — this is exactly the hop that broke the Python bus.
///
/// Extraction only. It says what was stated and about whom; it does not say
/// where that belongs. The address is CatalogerAgent's, one topic downstream
/// on events.facts, picked from a closed vocabulary rather than invented per
/// turn. The split is not tidiness: asked for the address as well, a small
/// model minted a new category for a fact it had already filed under another
/// one, and the file name is the whole index in this store.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: it publishes a list of records rather than one
/// parsed answer, and needs its own parse/filter, which doesn't fit that base
/// class's model. One substrate call per turn. No deterministic fallback
/// exists: only facts the LLM judges explicitly stated get extracted,
/// matching the Python prototype's Archivist, which relies entirely on
/// the same LLM discipline and may legitimately find nothing in a turn.
/// </summary>
public sealed class ArchivistAgent : AgentBase, ICognitiveAgent
{
    public const string ControlKindKey = "control.kind";
    public const string WrittenKind = "Written";

    /// <summary>What the flush actually put on disk, one "path = value" string per record — the same strings the log line prints, so the surface and the console agree without either reading the other.</summary>
    public const string WrittenRecordsKey = "archivist.written";

    /// <summary>The extracted, still-unaddressed facts, carried on the events.facts envelope's Meta.</summary>
    public const string FactsKey = "archivist.facts";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly ISubstrateProvider _substrate;
    private readonly SubstrateOptions _substrates;
    private readonly ILogger _logger;

    // The worked examples are well-formed "category=..." lines sitting in
    // the prompt, and on a message that states no fact at all the model
    // reaches for the nearest one and files it: measured at roughly half of
    // greetings and questions. The prompt already forbids this twice in
    // words and it holds only while a real fact is competing, so the rule
    // is enforced here instead. Derived from the instruction text rather
    // than hardcoded, so editing the examples moves the filter with them.
    private readonly Lazy<HashSet<string>> _exampleRows;

    public ArchivistAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ArchivistAgent> logger,
        ISubstrateProvider substrate, IOptions<SubstrateOptions> substrates,
        IInstructionStore instructions)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _instructions = instructions;
        _substrate = substrate;
        _substrates = substrates.Value;
        _logger = logger;
        _exampleRows = new Lazy<HashSet<string>>(() =>
            [.. ParseFacts(_instructions.For(Name), DateTimeOffset.MinValue).Select(Signature)]);
    }

    public override string Name => "Archivist";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // Reflection's own reposted ideas arrive back through events.perception
        // like any other turn — without this skip, Archivist would archive
        // the persona's own prior thought as if the user had said it.
        if (envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) == "self")
        {
            return;
        }

        var text = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        if (!_substrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No substrate entry for agent '{Name}' — add one to appsettings.json's Substrates:Agents section.");
        }

        // A deterministic-by-configuration Archivist extracts nothing, but
        // the envelope still goes out: Cataloger's write batch counts turns,
        // not facts, and a turn that never arrives holds the previous turn's
        // fact off disk indefinitely.
        if (!entry.UseSubstrate)
        {
            Publish(envelope, [], text);
            return;
        }

        var (newRecords, diagnostics) = await ExtractFactsAsync(envelope, text, cancellationToken).ConfigureAwait(false);

        // One line every turn, same shape as RecallAgent's aggregate line —
        // without this the only visible signal was a bare substrate-call
        // latency line, which says nothing about whether extraction actually
        // found a fact, so a silent parsing failure (e.g. the model using
        // "key: value" instead of the requested "key=value") looked
        // identical to a turn that legitimately had nothing to remember.
        if (diagnostics is not null)
        {
            var facts = newRecords.Count == 0 ? "nothing" : string.Join(", ", newRecords.Select(Describe));
            _logger.LogInformation("{Agent} {Facts} ({LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost)",
                Name, facts, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);
        }

        Publish(envelope, newRecords, text);
    }

    /// <summary>
    /// Text rides along explicitly: Envelope.Derive starts a fresh Meta
    /// rather than merging the parent's, and Cataloger needs the message to
    /// judge the fact against, by which time the speaker is long out of scope.
    /// </summary>
    private void Publish(Envelope envelope, IReadOnlyList<ArchiveRecord> facts, string text)
    {
        var meta = MetaBag.Empty.With(FactsKey, facts).With(PerceptionAgent.TextKey, text);
        _bus.Publish(Topics.Facts, envelope.Derive(Topics.Facts, Name, envelope.Severity, meta));
    }

    /// <summary>
    /// Whole row, not just the address: a copied example matches every
    /// field, while a real message about Lisbon rainfall would carry its own
    /// value and still be archived. Dropping on address alone would be a
    /// stronger filter that can discard a genuine fact.
    /// </summary>
    private static string Signature(ArchiveRecord r) => string.Join(Separator,
        new[] { r.Category, r.Topic, r.Subtopic, r.Subject, r.Key, r.Value }
            .Select(f => WhitespaceRun.Replace(f.Trim().ToLowerInvariant(), " ")));

    // A unit separator cannot appear in a field, so no combination of
    // field values can collide with a different row's signature.
    private const char Separator = '\u001f';

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Category and topic are empty at this stage — Cataloger fills them — so the log shows the fact, not a leading "//".</summary>
    private static string Describe(ArchiveRecord r) =>
        $"{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}";

    /// <summary>
    /// A broken or unavailable substrate call skips this turn's write
    /// entirely — errors are logged and swallowed, same posture as
    /// FallbackPosture.Closed on CognitiveAgent&lt;T&gt;.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics)> ExtractFactsAsync(Envelope envelope, string text, CancellationToken cancellationToken)
    {
        text = PromptCap.Apply(text);
        var prompt = InstructionFile.Fill(_instructions.For(Name), ("text", text));

        var started = Stopwatch.GetTimestamp();
        try
        {
            _logger.LogDebug("{Agent} extraction prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(Name, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} extraction response <<<\n{Response}", Name, result.Text);
            SubstrateTrace.Publish(_bus, envelope, Name, result);

            // The mock tier echoes the prompt back verbatim, and a reply
            // that contains its own entire input is never an extraction.
            // Cheap to keep, though the instruction it used to protect —
            // worked examples, every one a well-formed "category=..." line
            // waiting to be harvested back out — is gone: a real substrate
            // copied one and filed it as a fact every turn.
            if (result.Text.Contains(prompt, StringComparison.Ordinal))
            {
                return ([], result);
            }

            var parsed = ParseFacts(result.Text, envelope.Timestamp);
            var facts = parsed.Where(r => !_exampleRows.Value.Contains(Signature(r))).ToList();
            if (facts.Count < parsed.Count)
            {
                _logger.LogDebug("{Agent} discarded {Count} row(s) copied verbatim from the prompt's own examples",
                    Name, parsed.Count - facts.Count);
            }

            return (facts, result);
        }
        catch (Exception ex) when (!SubstrateHealth.IsShutdown(ex, cancellationToken))
        {
            // Nothing to retain: unlike Reflection's buffered turns, the
            // facts this call would have produced were never extracted, so
            // there is no raw material a retry could work from.
            var cause = SubstrateHealth.Classify(ex);
            _logger.LogWarning("{Agent} fact extraction {Cause}, skipping", Name, cause);
            SubstrateTrace.PublishFailure(_bus, envelope, Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, cause);
            return ([], null);
        }
    }

    // Was a split on "category=", which is no longer asked for. Subtopic
    // leads a well-formed line, but it is also the field the model drops
    // most often, so a block that turns out to hold two subjects is split
    // again below.
    private static readonly Regex FactBlockSplit = new(@"(?=\bsubtopic\s*[:=])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubjectBlockSplit = new(@"(?=\bsubject\s*[:=])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubjectMarker = new(@"\bsubject\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<ArchiveRecord> ParseFacts(string response, DateTimeOffset timestamp)
    {
        var records = new List<ArchiveRecord>();

        // Split on each field marker rather than on newlines: the requested
        // one-line-per-fact shape isn't reliable — a small model will just as
        // often put each key=value pair on its own line — so a fact's fields
        // are flattened back onto one line before parsing regardless of how
        // the response broke them up.
        foreach (var outer in FactBlockSplit.Split(response))
        {
            // Two subjects in one block means the subtopic marker went
            // missing on the second fact, not that one fact has two subjects.
            var blocks = SubjectMarker.Matches(outer).Count > 1 ? SubjectBlockSplit.Split(outer) : [outer];
            foreach (var block in blocks)
            {
                var line = string.Join(' ', block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (ParseFields(line) is not { } fields)
                {
                    continue;
                }

                var (subtopic, subject, key, value, sentence) = fields;

                // Written as the substrate wrote it. A validator may reject a
                // row; it may never edit one. Truncating a value here would
                // not stop a bad fact landing, it would store a corrupt one in
                // an append-only archive and serve the ellipsis back forever.
                //
                // Category and topic stay empty: this agent is not the one
                // that knows them. CatalogerAgent fills both before anything
                // reaches the store, and drops the fact if it cannot.
                records.Add(new ArchiveRecord(string.Empty, string.Empty, subtopic, subject, key, value,
                    timestamp, ArchiveDomain.External, Importance(key), sentence));
            }
        }

        return records;
    }

    private static readonly Regex ColonFieldPattern = new(
        @"\b(category|topic|subtopic|subject|key|value|sentence)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // \b matters: "subtopic=" contains "topic=", so a plain substring search
    // for the topic marker lands inside the subtopic one whenever the model
    // omits a standalone topic.
    private static readonly Regex FieldMarkerPattern = new(
        @"\b(category|topic|subtopic|subject|key|value|sentence)=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Values that occupy the slot without saying anything.</summary>
    private static bool IsEmptyWord(string value) => value.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "none" or "nothing" or "n/a" or "na" or "null" or "unknown" or "unspecified" or "-" => true,
        _ => false,
    };

    private static (string Subtopic, string Subject, string Key, string Value, string Sentence)? ParseFields(string line)
    {
        // Smaller models don't reliably stick to the requested "key=value"
        // shape and often write "key: value" instead — normalize that before
        // the fixed-marker split below rather than silently dropping every
        // line that deviates.
        line = ColonFieldPattern.Replace(line.TrimStart('-', '*', '•', ' ', '\t'), "$1=");

        // Field values may contain spaces (e.g. "subject=marcus holth"), so
        // split on the field markers rather than whitespace. Topic/
        // Subtopic (indices 1/2) are grouping labels the model sometimes
        // drops or duplicates (e.g. "topic=self topic=identity" with no
        // subtopic at all) — those default rather than losing the whole
        // fact; Category/Subject/Key/Value are the fact itself and stay
        // required.
        var names = new[] { "category", "topic", "subtopic", "subject", "key", "value", "sentence" };
        var matches = FieldMarkerPattern.Matches(line);

        // First occurrence wins, matching the previous IndexOf behaviour: a
        // duplicated marker is the model repeating itself, not a new field.
        var found = new Match?[names.Length];
        foreach (Match m in matches)
        {
            var idx = Array.FindIndex(names, n => string.Equals(n, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            found[idx] ??= m;
        }

        // Category and topic (0/1) are no longer asked for; a model that
        // volunteers one anyway still has to be split on, or the stray marker
        // ends up inside the neighbouring value.
        if (found[3] is null || found[4] is null || found[5] is null)
        {
            return null;
        }

        var present = Enumerable.Range(0, names.Length).Where(i => found[i] is not null).OrderBy(i => found[i]!.Index).ToArray();
        var values = new string?[names.Length];
        for (var i = 0; i < present.Length; i++)
        {
            var idx = present[i];
            var start = found[idx]!.Index + found[idx]!.Length;
            var end = i + 1 < present.Length ? found[present[i + 1]]!.Index : line.Length;
            values[idx] = line[start..end].Trim();
        }

        if (values[3]!.Length == 0 || values[4]!.Length == 0 || values[5]!.Length == 0)
        {
            return null;
        }

        // A model handed a shape will fill it. Told "nothing stated: reply
        // nothing", a small one still answers a bare "test" with
        // interaction/test/action/user/test = none -- the form completed,
        // no fact in it. Empty was already rejected above; this rejects the
        // words that mean empty.
        //
        // Instructing this away was tried and is worse: pressing the model
        // not to write an empty value pressed it into writing a full one,
        // and "I live in Trondheim" came back as Trondheim's annual
        // rainfall. A guard cannot hallucinate.
        //
        // The cost is real but small: "allergies = none" is a true fact
        // this drops. A fact that exists only as an absence is rare, and it
        // survives being said a second way ("no allergies").
        if (IsEmptyWord(values[5]!))
        {
            return null;
        }

        var subtopic = string.IsNullOrEmpty(values[2]) ? "general" : values[2]!;

        // Optional, unlike every other field that survives to here. A
        // missing sentence costs a row some retrieval surface; a required
        // one would cost the whole fact, and the fact is what this agent
        // exists to catch. Same reasoning that lets subtopic default rather
        // than drop the row.
        //
        // Last in the field order, so it runs to the end of the line and can
        // be an ordinary sentence with spaces in it — the marker split ends
        // it at the next field marker, and there is no next field.
        return (subtopic, values[3]!, values[4]!, values[5]!, values[6] ?? "");
    }

    /// <summary>
    /// Importance is scored per an explicit priority list rather than left
    /// to the LLM to infer a numeric scale: a name is more durably useful
    /// than a birthday/title, which in turn outranks an address.
    /// </summary>
    private static double Importance(string key)
    {
        var lowered = key.ToLowerInvariant();
        if (lowered.Contains("name"))
        {
            return 0.8;
        }

        if (lowered.Contains("birth") || lowered.Contains("title"))
        {
            return 0.6;
        }

        if (lowered.Contains("address"))
        {
            return 0.4;
        }

        return 0.5;
    }
}
