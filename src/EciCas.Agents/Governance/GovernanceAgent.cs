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
    private readonly ILogger<GovernanceAgent> _logger;
    private readonly GovernanceOptions _options;
    private readonly ConcurrentDictionary<Guid, BundleState> _bundles = new();

    public GovernanceAgent(IMessageBus bus, BusActivityTracker activity, ILogger<GovernanceAgent> logger, IOptions<GovernanceOptions> options)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _logger = logger;
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
        var state = _bundles.GetOrAdd(perception.CorrelationId, _ => new BundleState());

        bool justArrived;
        lock (state)
        {
            justArrived = state.Perception is null;
            state.Perception = perception;
        }

        if (justArrived)
        {
            _ = RunTimeoutAsync(state);
        }

        TryComplete(state);
    }

    private void OnAdvisory(Envelope advisory)
    {
        // GetOrAdd, not TryGetValue: Governance runs one consumer loop per
        // subscribed topic, so an advisory can arrive before this agent's own
        // Perception-topic loop has processed the originating event. Without
        // this, that advisory would be silently and permanently dropped.
        var state = _bundles.GetOrAdd(advisory.CorrelationId, _ => new BundleState());

        lock (state)
        {
            state.Advisories[advisory.PublishedBy] = advisory;
        }

        TryComplete(state);
    }

    private async Task RunTimeoutAsync(BundleState state)
    {
        try
        {
            await Task.Delay(_options.BundleTimeoutMs, state.TimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TryComplete(state, forced: true);
    }

    private void TryComplete(BundleState state, bool forced = false)
    {
        Envelope perception;
        lock (state)
        {
            if (state.Completed || state.Perception is null)
            {
                return;
            }

            if (!forced && !_options.BundleRoster.All(state.Advisories.ContainsKey))
            {
                return;
            }

            state.Completed = true;
            perception = state.Perception;
        }

        state.TimeoutCts.Cancel();

        if (forced)
        {
            var missing = _options.BundleRoster.Except(state.Advisories.Keys).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning("Governance bundle {CorrelationId} completed on timeout, missing advisors: {Missing}",
                    perception.CorrelationId, string.Join(", ", missing));
            }
        }

        var severity = SeverityExtensions.MaxOf(state.Advisories.Values.Select(a => a.Severity).Append(perception.Severity));
        var bundle = perception.Derive(Topics.Bundle, Name, severity, perception.Meta);
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

    private sealed class BundleState
    {
        public Envelope? Perception { get; set; }
        public Dictionary<string, Envelope> Advisories { get; } = [];
        public bool Completed { get; set; }
        public CancellationTokenSource TimeoutCts { get; } = new();
    }
}
