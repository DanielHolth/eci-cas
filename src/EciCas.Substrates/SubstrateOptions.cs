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
