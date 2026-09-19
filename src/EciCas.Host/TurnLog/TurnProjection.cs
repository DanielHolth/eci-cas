using EciCas.Agents.Toolkit;
using EciCas.Agents.Governance;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Agents.Sight;
using EciCas.Agents.Utterances;
using EciCas.Bus;
using EciCas.Core;

namespace EciCas.Host.TurnLog;

/// <summary>
/// Envelopes in, one TurnRecord out. Pure: no bus, no clock, no IO, no
/// state of its own, so the same projection serves the live surface, the
/// disk log, and a test that hands it three envelopes in the wrong order.
///
/// Wrong order is the normal case. The fan-out is concurrent by design, so
/// this fills slots as envelopes land and never appends in arrival order —
/// what a person reads is TurnRecord's field order, decided once here.
///
/// This is the one place that knows the meta-key table. Everything
/// downstream reads strings.
/// </summary>
public static class TurnProjection
{
    public static TurnRecord Apply(TurnRecord? current, Envelope envelope, long nextSeq)
    {
        var record = current ?? new TurnRecord
        {
            Seq = nextSeq,
            CorrelationId = envelope.CorrelationId,
            StartedAt = envelope.Timestamp,
            EndedAt = envelope.Timestamp,
        };

        // Every envelope moves the end of the event, including the ones that
        // arrive after the reply: Scribe's write and Reflection's batch
        // are part of what the turn cost, even though nobody waited for them.
        record = record with { EndedAt = Later(record.EndedAt, envelope.Timestamp) };

        return envelope.Topic switch
        {
            Topics.Perception => ApplyPerception(record, envelope),
            Topics.Advisories => ApplyAdvisory(record, envelope),
            Topics.Verdict => ApplyVerdict(record, envelope),
            Topics.Action => ApplyAction(record, envelope),
            Topics.SystemControl => ApplyControl(record, envelope),
            Topics.Telemetry => ApplyTelemetry(record, envelope),
            _ => record,
        };
    }

    private static TurnRecord ApplyPerception(TurnRecord record, Envelope envelope) => record with
    {
        Perception = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? record.Perception,

        // A pushed idea rides the same topic as something a person typed.
        // Only this key tells them apart, and drawing one as the other puts
        // words in the person's mouth.
        SelfTriggered = PerceptionAgent.IsBackground(envelope) || record.SelfTriggered,

        // A toolkit run reporting back: the links and the "name: ok" line
        // ride the perception itself, so they are filed under the turn that
        // voices them.
        References = [.. record.References, .. (envelope.Meta.Get<IReadOnlyList<ToolkitReference>>(ToolkitResult.ReferencesKey) ?? []).Select(r => new TurnReference(r.Title, r.Url, r.Summary))],
        Toolkits = envelope.Meta.Get<string>(ToolkitResult.NameKey) is { } tool
            ? [.. record.Toolkits, $"{tool}: {(envelope.Meta.Get<bool>(ToolkitResult.SuccessKey) ? "ok" : "failed")}"]
            : record.Toolkits,
        StartedAt = envelope.Timestamp,
    };

    private static TurnRecord ApplyAdvisory(TurnRecord record, Envelope envelope) => envelope.PublishedBy switch
    {
        "Impulse" => record with
        {
            Impulse = envelope.Meta.Get<string>(ImpulseAgent.AdviceKey) ?? record.Impulse,
            Expression = envelope.Meta.Get<string>(ImpulseAgent.ExpressionKey) ?? record.Expression,
        },
        // What she was told about the screen, which is the half of Sight's
        // work a reader cannot otherwise see: the OCR transcript is on disk
        // beside the screenshot, but the model's reading of the picture only
        // ever existed inside Intent's prompt.
        "Sight" => record with { Sight = envelope.Meta.Get<string>(SightAgent.AdviceKey) ?? record.Sight },
        "Recall" => record with { Reads = Describe(envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(ConsultAgent.RecalledFactsKey)) },
        "Hindsight" => record with { Hindsight = envelope.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NotesKey) ?? record.Hindsight },
        _ => record,
    };

    private static TurnRecord ApplyVerdict(TurnRecord record, Envelope envelope)
    {
        var verdict = envelope.Meta.Get<Verdict>(SecurityAgent.VerdictKey);
        if (verdict == Verdict.Green)
        {
            return record;
        }

        return record with
        {
            Verdict = verdict.ToString().ToLowerInvariant(),
            Concern = envelope.Meta.Get<string>(SecurityAgent.ConcernKey) ?? record.Concern,
        };
    }

    private static TurnRecord ApplyAction(TurnRecord record, Envelope envelope)
    {
        var verdict = envelope.Meta.Get<Verdict>(SecurityAgent.VerdictKey);
        return record with
        {
            Intent = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? record.Intent,
            Verdict = verdict == Verdict.Green ? record.Verdict : verdict.ToString().ToLowerInvariant(),

            // A block nudges Impulse and Governance re-reads the face
            // afterwards, so the action's word is fresher than the advisory's.
            Expression = envelope.Meta.Get<string>(GovernanceAgent.ExpressionKey) ?? record.Expression,
            Degraded = envelope.Meta.Get<bool>(GovernanceAgent.DegradedKey) || record.Degraded,
            Concluded = true,
        };
    }

    private static TurnRecord ApplyControl(TurnRecord record, Envelope envelope) => record with
    {
        Writes = envelope.Meta.Get<IReadOnlyList<string>>(ScribeAgent.WrittenRecordsKey) ?? record.Writes,
        WriteIds = envelope.Meta.Get<IReadOnlyList<string>>(ScribeAgent.WrittenIdsKey) ?? record.WriteIds,
        Passages = envelope.Meta.Get<IReadOnlyList<string>>(ReflectionAgent.PassagesKey) ?? record.Passages,
        Idea = envelope.Meta.Get<string>(ReflectionAgent.IdeaKey) ?? record.Idea,
    };

    private static TurnRecord ApplyTelemetry(TurnRecord record, Envelope envelope)
    {
        // Absent rather than zero: a provider that reports no token count is
        // not a call that used none. SubstrateTrace omits the key entirely
        // in that case, so presence is the question, not value.
        var meta = envelope.Meta;
        var call = new SubstrateCall(
            meta.Get<string>(SubstrateTrace.AgentKey) ?? envelope.PublishedBy,
            meta.Get<string>(SubstrateTrace.LabelKey),
            meta.Get<double>(SubstrateTrace.LatencyKey),
            meta.ContainsKey(SubstrateTrace.TokensKey) ? meta.Get<int>(SubstrateTrace.TokensKey) : null,
            meta.ContainsKey(SubstrateTrace.CostKey) ? meta.Get<decimal>(SubstrateTrace.CostKey) : null,
            meta.Get<string>(SubstrateHealth.DegradedKey),
            meta.Get<string>(SubstrateTrace.ProviderKey),
            meta.Get<string>(SubstrateTrace.ModelKey),
            meta.ContainsKey(SubstrateTrace.PromptTokensKey) ? meta.Get<int>(SubstrateTrace.PromptTokensKey) : null,
            meta.ContainsKey(SubstrateTrace.CompletionTokensKey) ? meta.Get<int>(SubstrateTrace.CompletionTokensKey) : null);

        return record with { Calls = [.. record.Calls, call] };
    }

    /// <summary>
    /// One line per recalled fact. Under the inverted archive a record is a
    /// sentence and every path field is empty, which through the old format
    /// string rendered as "//// = " -- four slashes and nothing, on every
    /// row. The sentence is what was recalled, so where there is one it is
    /// the line; the path form stays for records that still have a path.
    /// </summary>
    private static IReadOnlyList<string> Describe(IReadOnlyList<ArchiveRecord>? facts) =>
        facts is null ? [] : [.. facts.Select(r => string.IsNullOrWhiteSpace(r.Sentence)
            ? $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}"
            : string.IsNullOrWhiteSpace(r.Subject) || r.Sentence.StartsWith(r.Subject, StringComparison.OrdinalIgnoreCase)
                ? r.Sentence
                : $"{r.Subject}: {r.Sentence}")];

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
