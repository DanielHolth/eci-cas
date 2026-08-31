using System.Text.RegularExpressions;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Consolidator;

/// <summary>
/// Parallel publisher on events.bundle alongside Intent — never through the
/// live reply path (see plan's opening rationale: this is exactly the hop
/// that broke the Python bus). Batches bundle content into ArchiveRecords and
/// flushes to the store every BatchSize bundles, then announces the epoch on
/// system.control so Self can invalidate its persona cache.
///
/// Implements ICognitiveAgent directly rather than inheriting
/// CognitiveAgent&lt;T&gt;: results batch rather than publish one-shot, which
/// doesn't fit that base class's model. Every turn goes through one substrate
/// call (ExtractFactsAsync) grounded in Recall's own lookup results — already
/// present on this same Bundle envelope via GovernanceAgent.BuildBundleMeta —
/// biasing it to reuse paths Recall just showed it instead of minting
/// near-duplicates. No deterministic fallback write exists: only facts the
/// LLM judges explicitly stated get archived, matching the Python
/// prototype's Consolidator, which relies entirely on the same LLM
/// discipline and may legitimately write nothing for a turn.
/// </summary>
public sealed class ConsolidatorAgent : AgentBase, ICognitiveAgent
{
    public const string ControlKindKey = "control.kind";
    public const string EpochIdKey = "control.epoch_id";
    public const string WrittenKind = "Written";

    private readonly IMessageBus _bus;
    private readonly IArchiveStore _store;
    private readonly ISubstrateProvider _substrate;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly ConsolidatorOptions _options;
    private readonly ILogger _logger;
    private readonly List<ArchiveRecord> _pending = [];
    private readonly object _pendingLock = new();
    private int _bundlesSinceFlush;

    public ConsolidatorAgent(IMessageBus bus, BusActivityTracker activity, ILogger<ConsolidatorAgent> logger, IArchiveStore store,
        ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates, IOptions<ConsolidatorOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _substrate = substrate;
        _agentSubstrates = agentSubstrates.Value;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Consolidator";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Bundle];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // Reflection's own reposted ideas arrive back through events.perception
        // like any other turn — without this skip, Consolidator would archive
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

        // No deterministic fallback write: only what the LLM judges to be an
        // explicitly-stated fact gets archived (see ExtractFactsAsync's
        // prompt) — a turn with nothing worth remembering yields zero
        // records, same as the Python prototype's Consolidator.
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
            _logger.LogInformation("{Agent} {Facts} ({LatencyMs}ms, {Tokens} tokens, ${Cost} est. cost)",
                Name, facts, diagnostics.Latency.TotalMilliseconds, diagnostics.TokenCount, diagnostics.Cost);
        }

        // Flushes every BatchSize turns processed, not every BatchSize facts
        // extracted — most turns state nothing worth remembering, so counting
        // accumulated facts could leave a single just-stated fact (e.g. a
        // name) sitting invisible in memory for many turns, or lost entirely
        // on restart, waiting for enough *other* facts to come along.
        List<ArchiveRecord>? batch = null;
        lock (_pendingLock)
        {
            _pending.AddRange(newRecords);
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

        await _store.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Agent} wrote {Count} records: {Paths}",
            Name, batch.Count, string.Join(", ", batch.Select(r => $"{r.Category}/{r.Topic}/{r.Subtopic}")));

        var epochId = Guid.NewGuid();
        var written = envelope.Derive(Topics.SystemControl, Name, envelope.Severity,
            MetaBag.Empty.With(ControlKindKey, WrittenKind).With(EpochIdKey, epochId));
        _bus.Publish(Topics.SystemControl, written);
    }

    /// <summary>
    /// A broken or unavailable substrate call skips this turn's write
    /// entirely — errors are logged and swallowed, same posture as
    /// FallbackPosture.Closed on CognitiveAgent&lt;T&gt;.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRecord> Facts, SubstrateResult? Diagnostics)> ExtractFactsAsync(Envelope envelope, string text, string substrateClass, CancellationToken cancellationToken)
    {
        var selected = envelope.Meta.Get<IReadOnlyList<ArchivePair>>(ReasoningAgent.SelectedPairsKey) ?? [];
        var known = selected.Count == 0
            ? "none"
            : string.Join(", ", selected.Select(t => $"{t.Category}/{t.Topic}"));
        text = PromptCap.Apply(text);
        var prompt = $"""
            Extract every fact the user explicitly stated about themselves or
            someone/something else in this turn — a name, a place, a
            relationship, a preference, anything concrete about a real
            person/place/thing. Do not infer, guess, or embellish beyond what
            was said. A turn with an obvious stated fact (e.g. "my name is
            X") must never come back empty.

            Never extract meta-commentary about the turn or message itself —
            no facts like "purpose", "topic", or "intent" of what was said,
            and no facts derived from filler, small talk, or test/placeholder
            text (e.g. "this is a test", "hello", "ok"). If the turn contains
            no concrete real-world fact, that is the normal, expected case —
            respond with nothing rather than inventing something to say.

            Respond with one line per fact:
            category=... topic=... subtopic=... subject=... key=... value=...

            Category (1 word), Topic (1 word), Subtopic (1-2 words) group the
            fact — reuse one of these existing category/topic groups when it
            clearly fits:
            {known}
            Otherwise invent a new one; there being no existing match is not
            a reason to skip the fact. Subject (1-2 words) is usually a
            unique entity, named — for a fact about the user themselves, use
            their own name once stated, or "owner" before it's known. Key
            (1-3 words) is the attribute. Value ({ArchiveWriteStyle.TerseValue})
            is the content itself.

            {ArchiveWriteStyle.EnglishFields}

            Examples:
            category=person topic=family subtopic=owner subject=daniel key=name value=daniel
            category=person topic=family subtopic=son subject=marcus holth key=birthdate value=2020-08-28
            category=event topic=wedding subtopic=family subject=maria holth key=location value=drammen kirke

            Only if truly nothing was stated, respond with nothing.

            Turn: {text}
            """;

        try
        {
            var result = await _substrate.CompleteAsync(substrateClass, prompt, cancellationToken).ConfigureAwait(false);

            // The mock tier echoes the prompt back verbatim — parsing that
            // would just re-harvest our own worked examples (each one a
            // valid "category=..." line) out of the instructions as if
            // they'd been extracted. A real substrate never reproduces its
            // entire multi-hundred-char input inside a reply.
            if (result.Text.Contains(prompt, StringComparison.Ordinal))
            {
                return ([], result);
            }

            return (ParseFacts(result.Text, envelope.Timestamp), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} fact-extraction substrate call failed, skipping", Name);
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
            records.Add(new ArchiveRecord(category, topic, subtopic, subject, key, PromptCap.Apply(value), timestamp, ArchiveDomain.External, importance));
        }

        return records;
    }

    private static readonly Regex ColonFieldPattern = new(
        @"\b(category|topic|subtopic|subject|key|value)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string Category, string Topic, string Subtopic, string Subject, string Key, string Value)? ParseFields(string line)
    {
        // Smaller models don't reliably stick to the requested "key=value"
        // shape and often write "key: value" instead — normalize that before
        // the fixed-marker split below rather than silently dropping every
        // line that deviates.
        line = ColonFieldPattern.Replace(line.TrimStart('-', '*', '•', ' ', '\t'), "$1=");

        // Field values may contain spaces (e.g. "subject=marcus holth"), so
        // split on the fixed key= markers rather than whitespace. Topic/
        // Subtopic (indices 1/2) are grouping labels the model sometimes
        // drops or duplicates (e.g. "topic=self topic=identity" with no
        // subtopic at all) — those default rather than losing the whole
        // fact; Category/Subject/Key/Value are the fact itself and stay
        // required.
        var keys = new[] { "category=", "topic=", "subtopic=", "subject=", "key=", "value=" };
        var indices = keys.Select(k => line.IndexOf(k, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (indices[0] < 0 || indices[3] < 0 || indices[4] < 0 || indices[5] < 0)
        {
            return null;
        }

        var present = Enumerable.Range(0, keys.Length).Where(i => indices[i] >= 0).OrderBy(i => indices[i]).ToArray();
        var values = new string?[keys.Length];
        for (var i = 0; i < present.Length; i++)
        {
            var idx = present[i];
            var start = indices[idx] + keys[idx].Length;
            var end = i + 1 < present.Length ? indices[present[i + 1]] : line.Length;
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
