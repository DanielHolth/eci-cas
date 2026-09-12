namespace EciCas.Core;

/// <summary>
/// Agent -> vendor mapping, one place. `Providers` holds shared endpoint
/// config per vendor (never a literal key — only the env var that holds
/// it), `Agents` says which provider and model backs each cognitive agent,
/// by that agent's own name. Multiple providers can be live at once — e.g.
/// Librarian on Mistral, Archivist on OpenAI — because each agent picks its
/// own provider independently.
///
/// There is no class layer in between. Buckets named fast-low/slow-high were
/// a vocabulary the roles had to be translated into and back out of; a tier
/// now says what backs Librarian by saying "Librarian".
/// </summary>
public sealed class SubstrateOptions
{
    public Dictionary<string, ProviderEndpoint> Providers { get; set; } = [];

    public Dictionary<string, SubstrateAgentEntry> Agents { get; set; } = [];
}

public sealed class ProviderEndpoint
{
    public string BaseUrl { get; set; } = "";
    public string ApiKeyEnvironmentVariable { get; set; } = "";

    /// <summary>
    /// How long a single call may hang before it counts as timed out. A
    /// minute of silence followed by an apology is worse than the apology
    /// alone: agents fall back cleanly, so the ceiling is a latency budget,
    /// not a correctness one. Tune per tier — a slow reasoning model needs
    /// more headroom than a fast picking one.
    /// </summary>
    public int TimeoutMs { get; set; } = 20_000;

    /// <summary>
    /// After a transport failure, fail this provider's calls instantly for
    /// this long instead of making every agent in the fan-out re-discover
    /// the same dead endpoint at full timeout cost. The next call after the
    /// window is a live probe: one success closes the circuit again.
    /// Zero disables the breaker.
    /// </summary>
    public int CircuitOpenMs { get; set; } = 5_000;

    /// <summary>
    /// Ceiling on in-flight calls to this provider; 0 means unlimited, which
    /// is right for a vendor API that scales on its own. A single local model
    /// backing every class is the opposite case: the Recall fan-out would
    /// queue inside the server anyway, so it queues here instead, where a
    /// cancelled turn abandons its place in line. The wait is deliberately
    /// outside the HttpClient timeout — queue time is not the model hanging.
    /// </summary>
    public int MaxConcurrent { get; set; }
}

public sealed class SubstrateAgentEntry
{
    /// <summary>A key into <see cref="SubstrateOptions.Providers"/>, or "mock" for the zero-cost default.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>Vendor model id. Falls back to the agent's own name when unset.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// OpenAI reasoning_effort ("low"/"medium"/"high"), sent only when set —
    /// omitted entirely for providers/models that don't take it (e.g. the
    /// Mistral-backed agents leave this unset in appsettings).
    /// https://developers.openai.com/api/docs/guides/reasoning
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Output ceiling, sent as max_tokens only when set. A vendor API leaves
    /// this unset and bills what it writes; an uncapped local model can
    /// ramble for minutes instead. Headroom, not permission — the
    /// instruction file is what keeps a reply short.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Qwen-style chat_template_kwargs.enable_thinking, sent only when set so
    /// nothing changes for providers that don't take it. Qwen3 reasons aloud
    /// by default, which the picking agents must not do; the writing agents
    /// want it, and land behind the reply anyway. Requires llama-server
    /// --jinja for the flag to reach the template.
    /// </summary>
    public bool? Thinking { get; set; }

    /// <summary>
    /// False means this agent never calls a substrate at all — it publishes
    /// its fallback directly. The provider/model fields stay meaningful
    /// (they say what it would use if switched back on), so turning an agent
    /// off is one word rather than a deleted entry.
    /// </summary>
    public bool UseSubstrate { get; set; } = true;

    /// <summary>Raw $/million-token pricing, pasted straight off the provider's pricing page — no manual per-token conversion needed.</summary>
    public PricePerMillionTokens? PricePerMtok { get; set; }

    /// <summary>
    /// Cost of one call, priced per direction. Output runs several times the
    /// price of input on every provider we use, and this system is heavily
    /// input-weighted — a six-turn window and a fistful of advisories go in,
    /// two sentences come out — so a blended rate applied to the total is not
    /// an approximation, it is an overcharge of roughly the output/input
    /// ratio. The energy meter debits this number, so being wrong here means
    /// draining a person's day at the wrong speed.
    /// Zero (not priced) when PricePerMtok is unset, e.g. "mock".
    /// </summary>
    public decimal CostUsd(int promptTokens, int completionTokens) =>
        PricePerMtok is { } p
            ? (promptTokens * p.Input + completionTokens * p.Output) / 1_000_000m
            : 0m;
}

public sealed class PricePerMillionTokens
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
}
