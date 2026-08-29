using System.Collections.Concurrent;
using EciCas.Agents.Intent;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Governance;

/// <summary>
/// Decision-only: bundles the advisory fan-out, gates Action on Security's
/// verdict, and produces the conclusion. Nothing else — see plan §3.3.
/// </summary>
public sealed class GovernanceAgent : AgentBase
{
    private readonly IMessageBus _bus;
    private readonly GovernanceOptions _options;
    private readonly ConcurrentDictionary<Guid, BundleState> _bundles = new();

    public GovernanceAgent(IMessageBus bus, BusActivityTracker activity, ILogger<GovernanceAgent> logger, IOptions<GovernanceOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _options = options.Value;
    }

    public override string Name => "Governance";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.Advisories, Topics.Verdict];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Topic)
        {
            case Topics.Perception:
                OnPerception(envelope);
                break;
            case Topics.Advisories:
                OnAdvisory(envelope);
                break;
            case Topics.Verdict:
                OnVerdict(envelope);
                break;
        }

        return Task.CompletedTask;
    }

    private void OnPerception(Envelope perception)
    {
        var state = _bundles.GetOrAdd(perception.CorrelationId, _ => new BundleState(perception));
        TryComplete(state);
    }

    private void OnAdvisory(Envelope advisory)
    {
        if (!_bundles.TryGetValue(advisory.CorrelationId, out var state))
        {
            return;
        }

        lock (state)
        {
            state.Advisories[advisory.PublishedBy] = advisory;
        }

        TryComplete(state);
    }

    private void TryComplete(BundleState state)
    {
        lock (state)
        {
            if (state.Completed)
            {
                return;
            }

            if (!_options.BundleRoster.All(state.Advisories.ContainsKey))
            {
                return;
            }

            state.Completed = true;
        }

        var severity = SeverityExtensions.MaxOf(state.Advisories.Values.Select(a => a.Severity).Append(state.Perception.Severity));
        var bundle = state.Perception.Derive(Topics.Bundle, Name, severity, state.Perception.Meta);
        _bus.Publish(Topics.Bundle, bundle);
    }

    private void OnVerdict(Envelope verdict)
    {
        var value = verdict.Meta.Get<Verdict>(SecurityAgent.VerdictKey);
        var reply = verdict.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;

        if (value == Verdict.Green)
        {
            var action = verdict.Derive(Topics.Action, Name, verdict.Severity, MetaBag.Empty.With(IntentAgent.ReplyKey, reply));
            _bus.Publish(Topics.Action, action);
        }

        var conclusion = verdict.Derive(Topics.Conclusion, Name, verdict.Severity, MetaBag.Empty.With(IntentAgent.ReplyKey, reply).With(SecurityAgent.VerdictKey, value));
        _bus.Publish(Topics.Conclusion, conclusion);

        _bundles.TryRemove(verdict.CorrelationId, out _);
    }

    private sealed class BundleState(Envelope perception)
    {
        public Envelope Perception { get; } = perception;
        public Dictionary<string, Envelope> Advisories { get; } = [];
        public bool Completed { get; set; }
    }
}
