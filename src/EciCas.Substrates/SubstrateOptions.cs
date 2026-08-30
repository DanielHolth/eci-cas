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

    public decimal CostPerTokenUsd { get; set; }
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
}
