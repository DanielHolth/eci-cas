using EciCas.Agents.Archivist;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
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
        // arrive after the reply: Archivist's write and Reflection's batch
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
        ProfileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey) ?? record.ProfileId,

        // A pushed idea rides the same topic as something a person typed.
        // Only this key tells them apart, and drawing one as the other puts
        // words in the person's mouth.
        SelfTriggered = envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) == "self" || record.SelfTriggered,
        StartedAt = envelope.Timestamp,
    };

    private static TurnRecord ApplyAdvisory(TurnRecord record, Envelope envelope) => envelope.PublishedBy switch
    {
        "Impulse" => record with { Impulse = envelope.Meta.Get<string>(ImpulseAgent.AdviceKey) ?? record.Impulse },
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
            Concluded = true,
        };
    }

    private static TurnRecord ApplyControl(TurnRecord record, Envelope envelope) => record with
    {
        Writes = envelope.Meta.Get<IReadOnlyList<string>>(ArchivistAgent.WrittenRecordsKey) ?? record.Writes,
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
            meta.Get<string>(SubstrateHealth.DegradedKey));

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
            : r.Sentence)];

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
