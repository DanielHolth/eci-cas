namespace EciCas.Substrates;

/// <summary>
/// Substrate classes -> vendor mapping, one place. Mirrors the Python
/// prototype's ecosystem-manifest.yaml `substrates:` table: `Providers` holds
/// shared endpoint config per vendor (never a literal key — only the env var
/// that holds it), `Classes` says which provider and model backs each
/// logical substrate class. Multiple providers can be live at once — e.g.
/// fast-* on Mistral, slow-* on OpenAI — because each class picks its own
/// provider independently.
/// </summary>
public sealed class SubstrateOptions
{
    public Dictionary<string, ProviderEndpoint> Providers { get; set; } = [];

    public Dictionary<string, SubstrateClassEntry> Classes { get; set; } = [];
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
}

public sealed class SubstrateClassEntry
{
    /// <summary>A key into <see cref="SubstrateOptions.Providers"/>, or "mock" for the zero-cost default.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>Vendor model id. Falls back to the substrate class name itself when unset.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// OpenAI reasoning_effort ("low"/"medium"/"high"), sent only when set —
    /// omitted entirely for providers/models that don't take it (e.g.
    /// Mistral's fast-* classes leave this unset in appsettings).
    /// https://developers.openai.com/api/docs/guides/reasoning
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>Raw $/million-token pricing, pasted straight off the provider's pricing page — no manual per-token conversion needed.</summary>
    public PricePerMillionTokens? PricePerMtok { get; set; }

    /// <summary>
    /// Blended per-token cost derived from PricePerMtok, computed once
    /// wherever this entry is bound rather than asking every appsettings.json
    /// to carry a pre-converted per-token rate that varies by model. Simple
    /// average of input/output rather than tracking the two token counts
    /// separately through SubstrateResult — good enough for a cost estimate.
    /// Zero (not priced) when PricePerMtok is unset, e.g. "mock".
    /// </summary>
    public decimal CostPerTokenUsd => PricePerMtok is { } p ? (p.Input + p.Output) / 2m / 1_000_000m : 0m;
}

public sealed class PricePerMillionTokens
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
}
