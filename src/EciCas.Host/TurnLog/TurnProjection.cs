using EciCas.Agents.Archivist;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Librarian;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
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
            Topics.SelectedPairs => ApplySelectedPairs(record, envelope),
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

    /// <summary>
    /// What Librarian judged worth opening. Kept apart from Reads because
    /// they answer different questions — a turn that selected three pairs and
    /// recalled nothing looked, in the drawer, exactly like a turn where
    /// Librarian never ran.
    /// </summary>
    private static TurnRecord ApplySelectedPairs(TurnRecord record, Envelope envelope)
    {
        var pairs = envelope.Meta.Get<IReadOnlyList<ArchivePair>>(LibrarianAgent.SelectedPairsKey);
        return pairs is null ? record : record with { Pairs = [.. pairs.Select(p => $"{p.Category}/{p.Topic}")] };
    }

    private static TurnRecord ApplyAdvisory(TurnRecord record, Envelope envelope) => envelope.PublishedBy switch
    {
        "Impulse" => record with { Impulse = envelope.Meta.Get<string>(ImpulseAgent.AdviceKey) ?? record.Impulse },
        "Recall" => record with { Reads = Describe(envelope.Meta.Get<IReadOnlyList<ArchiveRecord>>(RecallAgent.RecalledFactsKey)) },
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
            meta.Get<string>(SubstrateTrace.ClassKey) ?? string.Empty,
            meta.Get<string>(SubstrateTrace.LabelKey),
            meta.Get<double>(SubstrateTrace.LatencyKey),
            meta.ContainsKey(SubstrateTrace.TokensKey) ? meta.Get<int>(SubstrateTrace.TokensKey) : null,
            meta.ContainsKey(SubstrateTrace.CostKey) ? meta.Get<decimal>(SubstrateTrace.CostKey) : null,
            meta.Get<string>(SubstrateHealth.DegradedKey));

        return record with { Calls = [.. record.Calls, call] };
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<ArchiveRecord>? facts) =>
        facts is null ? [] : [.. facts.Select(r => $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}")];

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
