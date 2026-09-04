using EciCas.Agents.Archivist;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Identity;

/// <summary>
/// Was Personality in the Python prototype, then Self, now Identity. The
/// name kept shrinking toward what the code does: this is thin — a cached
/// snippet with a fallback, no substrate call — and "Self" promised
/// selfhood it does not deliver. Identity is also the name the persona and
/// avatar picker will want when persona editing lands, at which point the
/// agent gets thicker and the name still fits.
///
/// Identity lookup: a persona snippet read from IArchiveStore and cached,
/// re-hydrated whenever Archivist announces a new epoch on
/// system.control (a write could have touched the persona record). Falls
/// back to a fixed snippet when the store has nothing under "assistant/persona"
/// yet — nothing writes persona records in this pass, so that's the only
/// path exercised today; the cache/invalidation plumbing is real and ready
/// for whenever persona editing lands. Deterministic tier either way — no
/// substrate call, so no CognitiveAgent&lt;T&gt; base.
/// </summary>
public sealed class IdentityAgent : AgentBase
{
    public const string AdviceKey = "identity.advice";

    /// <summary>
    /// "assistant/persona", not "assistant/identity": that pair already
    /// exists in the parquet archive holding identity facts, and one address
    /// meaning two things in two stores is exactly the ambiguity the rename
    /// away from "self" was for. This is the snippet, in the JSONL agent
    /// state store; the facts are rows, in parquet.
    /// </summary>
    public const string IdentityPath = "assistant/persona";

    /// <summary>What a persona that has lost its own description says instead.</summary>
    public const string StrangerSection = "stranger";

    /// <summary>How the persona's own name is phrased to Intent. Prose, so revising it is not a rebuild.</summary>
    public const string NameSection = "name";

    private readonly IMessageBus _bus;
    private readonly IAgentStateStore _store;
    private readonly IInstructionStore _instructions;
    private readonly PersonaName _names;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _cachedIdentity;

    public IdentityAgent(IMessageBus bus, BusActivityTracker activity, ILogger<IdentityAgent> logger, IAgentStateStore store,
        IInstructionStore instructions, PersonaName names)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _store = store;
        _instructions = instructions;
        _names = names;
    }

    public override string Name => "Identity";
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

        // The tone is one persona for the whole device; the name belongs to
        // whoever is talking. They arrive as one line because Intent reads one
        // bracketed aside, not a structure.
        var profileId = envelope.Meta.Get<string>(PerceptionAgent.ProfileKey);
        var name = await _names.ForAsync(profileId, cancellationToken).ConfigureAwait(false);
        var advice = $"{identity} {InstructionFile.Fill(_instructions.For(Name, NameSection), ("name", name))}";

        var advisory = envelope.Derive(Topics.Advisories, Name, envelope.Severity, MetaBag.Empty.With(AdviceKey, advice));
        _bus.Publish(Topics.Advisories, advisory);
    }

    private async Task OnControlAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Meta.Get<string>(ArchivistAgent.ControlKindKey) != ArchivistAgent.WrittenKind)
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

        _names.Forget();
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
            _cachedIdentity = records.Count > 0 ? records[0].Content : _instructions.For(Name, StrangerSection);
            return _cachedIdentity;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
