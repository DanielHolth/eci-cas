using System.Text.RegularExpressions;
using EciCas.Agents.Perception;
using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
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
/// Batches bundle content into ArchiveRecords and
/// flushes to the store every BatchSize bundles, then announces the epoch on
/// system.control so Identity can invalidate its persona cache.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: results batch rather than publish one-shot, which
/// doesn't fit that base class's model. Every turn goes through one substrate
/// call (ExtractFactsAsync) grounded in the pairs Librarian selected —
/// already present on this same Bundle envelope via
/// GovernanceAgent.BuildBundleMeta — biasing it to reuse existing paths
/// instead of minting near-duplicates. No deterministic fallback write
/// exists: only facts the LLM judges explicitly stated get archived,
/// matching the Python prototype's Archivist, which relies entirely on
/// the same LLM discipline and may legitimately write nothing for a turn.
/// </summary>
public sealed class ArchivistAgent : AgentBase, ICognitiveAgent
{
    public const string ControlKindKey = "control.kind";
    public const string WrittenKind = "Written";

    private readonly IMessageBus _bus;
    private readonly IInstructionStore _instructions;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ArchivistOptions _options;
    private readonly ILogger _logger;
    // Pending facts carry the profile that stated them: a batch can span
    // turns, and by the time it flushes the speaker is long gone from scope.
    private readonly List<(string? ProfileId, ArchiveRecord Record)> _pending = [];
    private readonly object _pendingLock = new();
    private int _bundlesSinceFlush;

    public ArchivistAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ArchivistAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ArchivistOptions> options,
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

        if (!_agentSubstrates.Agents.TryGetValue(Name, out var entry))
        {
            throw new InvalidOperationException($"No AgentSubstrates entry for agent '{Name}' — add one to appsettings.json's AgentSubstrates:Agents section.");
        }

        // A deterministic-by-configuration Archivist archives nothing:
        // there is no keyword extractor to fall back to, and inventing one
        // would put facts on record that nobody judged to be facts.
        if (!entry.UseSubstrate)
        {
            return;
        }

        // No deterministic fallback write: only what the LLM judges to be an
        // explicitly-stated fact gets archived (see ExtractFactsAsync's
        // prompt) — a turn with nothing worth remembering yields zero
        // records, same as the Python prototype's Archivist.
        var (newRecords, diagnostics) = await ExtractFactsAsync(envelope, text, entry.Class, cancellationToken).ConfigureAwait(false);

        // One line every turn, same shape as RecallAgent's aggregate line —
        // without this the only visible signal was a bare substrate-call
        // latency line, which says nothing about whether extraction actually
        // found a fact, so a silent parsing failure (e.g. the model using
        // "key: value" instead of the requested "key=value") looked
        // identical to a turn that legitimately had nothing to remember.
        if (diagnostics is not null)
        {
            var facts = newRecords.Count == 0 ? "nothing" : string.Join(", ", newRecords.Select(r => $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}"));
            _logger.LogInformation("{Agent} {Facts} [{Class}] ({LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost)",
                Name, facts, entry.Class, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);
        }

        // Flushes every BatchSize turns processed, not every BatchSize facts
        // extracted — most turns state nothing worth remembering, so counting
        // accumulated facts could leave a single just-stated fact (e.g. a
        // name) sitting invisible in memory for many turns, or lost entirely
        // on restart, waiting for enough *other* facts to come along.
        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        List<(string? ProfileId, ArchiveRecord Record)>? batch = null;
        lock (_pendingLock)
        {
            _pending.AddRange(newRecords.Select(r => (profileId, r)));
            _bundlesSinceFlush++;
            if (_bundlesSinceFlush >= _options.BatchSize && _pending.Count > 0)
            {
                batch = [.. _pending];
                _pending.Clear();
                _bundlesSinceFlush = 0;
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
        _logger.LogInformation("{Agent} wrote {Count} records: {Paths}",
            Name, batch.Count, string.Join(", ", batch.Select(p => $"{p.Record.Category}/{p.Record.Topic}/{p.Record.Subtopic}/{p.Record.Subject}/{p.Record.Key} = {p.Record.Value}")));

        var written = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ControlKindKey, WrittenKind));
        _bus.Publish(Topics.SystemControl, written);
    }

    /// <summary>
    /// A broken or unavailable substrate call skips this turn's write
    /// entirely — errors are logged and swallowed, same posture as
    /// FallbackPosture.Closed on CognitiveAgent&lt;T&gt;.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics)> ExtractFactsAsync(Envelope envelope, string text, string substrateClass, CancellationToken cancellationToken)
    {
        var selected = envelope.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey) ?? [];
        var known = selected.Count == 0
            ? "none"
            : string.Join(", ", selected.Select(t => $"{t.Category}/{t.Topic}"));
        text = PromptCap.Apply(text);
        var prompt = InstructionFile.Fill(_instructions.For(Name),
            ("known", known),
            ("terse", ArchiveWriteStyle.TerseValue),
            ("text", text));

        try
        {
            _logger.LogDebug("{Agent} extraction prompt >>>\n{Prompt}", Name, prompt);
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Agent} extraction response <<<\n{Response}", Name, result.Text);

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

            return (ParseFacts(result.Text, envelope.Timestamp), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to retain: unlike Reflection's buffered turns, the
            // facts this call would have produced were never extracted, so
            // there is no raw material a retry could work from.
            _logger.LogWarning("{Agent} fact extraction {Cause}, skipping", Name, SubstrateHealth.Classify(ex));
            return ([], null);
        }
    }

    /// <summary>
    /// Importance is scored per an explicit priority list rather than left
    /// to the LLM to infer a numeric scale: a name is more durably useful
    /// than a birthday/title, which in turn outranks an address.
    /// </summary>
    private static readonly Regex CategoryBlockSplit = new(@"(?=\bcategory\s*[:=])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<ArchiveRecord> ParseFacts(string response, DateTimeOffset timestamp)
    {
        var records = new List<ArchiveRecord>();

        // Split on each "category=" marker rather than on newlines: the
        // requested one-line-per-fact shape isn't reliable — a small model
        // will just as often put each key=value pair on its own line — so a
        // fact's fields are flattened back onto one line before parsing
        // regardless of how the response broke them up.
        foreach (var block in CategoryBlockSplit.Split(response))
        {
            var line = string.Join(' ', block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var fields = ParseFields(line);
            if (fields is null)
            {
                continue;
            }

            var (category, topic, subtopic, subject, key, value) = fields.Value;
            var importance = Importance(key);
            // Written as the substrate wrote it. A validator may reject a row;
            // it may never edit one. Truncating a value here would not stop a
            // bad fact landing, it would store a corrupt one in an
            // append-only archive and serve the ellipsis back forever.
            records.Add(new ArchiveRecord(category, topic, subtopic, subject, key, value, timestamp, ArchiveDomain.External, importance));
        }

        return records;
    }

    private static readonly Regex ColonFieldPattern = new(
        @"\b(category|topic|subtopic|subject|key|value)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // \b matters: "subtopic=" contains "topic=", so a plain substring search
    // for the topic marker lands inside the subtopic one whenever the model
    // omits a standalone topic.
    private static readonly Regex FieldMarkerPattern = new(
        @"\b(category|topic|subtopic|subject|key|value)=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string Category, string Topic, string Subtopic, string Subject, string Key, string Value)? ParseFields(string line)
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
        var names = new[] { "category", "topic", "subtopic", "subject", "key", "value" };
        var matches = FieldMarkerPattern.Matches(line);

        // First occurrence wins, matching the previous IndexOf behaviour: a
        // duplicated marker is the model repeating itself, not a new field.
        var found = new Match?[names.Length];
        foreach (Match m in matches)
        {
            var idx = Array.FindIndex(names, n => string.Equals(n, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            found[idx] ??= m;
        }

        if (found[0] is null || found[3] is null || found[4] is null || found[5] is null)
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

        if (values[0]!.Length == 0 || values[3]!.Length == 0 || values[4]!.Length == 0 || values[5]!.Length == 0)
        {
            return null;
        }

        var topic = string.IsNullOrEmpty(values[1]) ? "general" : values[1]!;
        var subtopic = string.IsNullOrEmpty(values[2]) ? "general" : values[2]!;
        return (values[0]!, topic, subtopic, values[3]!, values[4]!, values[5]!);
    }

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
