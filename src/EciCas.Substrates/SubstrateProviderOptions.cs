namespace EciCas.Substrates;

/// <summary>
/// Config for the live OpenAI-compatible HTTP provider. The API key is never
/// read from config directly — only the name of the environment variable that
/// holds it, so the manifest can be committed without secrets.
/// </summary>
public sealed class SubstrateProviderOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";

    /// <summary>Substrate class (e.g. "fast-low") -> concrete model name.</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    public decimal CostPerTokenUsd { get; set; }
}
