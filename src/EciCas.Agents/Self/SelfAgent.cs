using EciCas.Agents.Consolidator;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Self;

/// <summary>
/// Identity lookup: a persona snippet read from IArchiveStore and cached,
/// re-hydrated whenever Consolidator announces a new epoch on
/// system.control (a write could have touched the persona record). Falls
/// back to a fixed snippet when the store has nothing under "self/identity"
/// yet — nothing writes persona records in this pass, so that's the only
/// path exercised today; the cache/invalidation plumbing is real and ready
/// for whenever persona editing lands. Deterministic tier either way — no
/// substrate call, so no CognitiveAgent&lt;T&gt; base.
/// </summary>
public sealed class SelfAgent : AgentBase
{
    public const string AdviceKey = "self.advice";

    private const string IdentityPath = "self/identity";
    private const string DefaultIdentitySnippet = "I'm ECI, here to help.";

    private readonly IMessageBus _bus;
    private readonly IAgentStateStore _store;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _cachedIdentity;

    public SelfAgent(IMessageBus bus, BusActivityTracker activity, ILogger<SelfAgent> logger, IAgentStateStore store)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
    }

    public override string Name => "Self";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.SystemControl];

    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Topic)
        {
            case Topics.Perception:
                await OnPerceptionAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Topics.SystemControl:
                await OnControlAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task OnPerceptionAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var identity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(AdviceKey, identity));
        _bus.Publish(Topics.Advisories, advisory);
    }

    private async Task OnControlAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Meta.Get<string>(ConsolidatorAgent.ControlKindKey) != ConsolidatorAgent.WrittenKind)
        {
            return;
        }

        // Must take the same lock GetIdentityAsync holds across its store
        // call — otherwise an invalidation landing between that call
        // returning and the cache assignment gets silently overwritten by
        // the now-stale result.
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedIdentity = null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<string> GetIdentityAsync(CancellationToken cancellationToken)
    {
        if (_cachedIdentity is { } cached)
        {
            return cached;
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedIdentity is { } cachedAfterLock)
            {
                return cachedAfterLock;
            }

            var records = await _store.LookupAsync([IdentityPath], maxPerPath: 1, cancellationToken).ConfigureAwait(false);
            _cachedIdentity = records.Count > 0 ? records[0].Content : DefaultIdentitySnippet;
            return _cachedIdentity;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
