namespace EciCas.Agents.Toolkit;

/// <summary>
/// The web search toolkit's own switches. Whether toolkits run at all on a
/// tier is <see cref="ToolkitOptions.Enabled"/>; this is the narrower "may a
/// question leave the machine for a search engine".
/// </summary>
public sealed class SearchOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>"duckduckgo" (keyless, scrapes the HTML endpoint) or "brave" (API key).</summary>
    public string Provider { get; set; } = "duckduckgo";

    public int MaxResults { get; set; } = 5;

    /// <summary>Longest query sent out; a spoken turn can ramble and the engine only needs the gist.</summary>
    public int MaxQueryChars { get; set; } = 200;

    public string BraveKeyEnvironmentVariable { get; set; } = "BRAVE_API_KEY";
}
