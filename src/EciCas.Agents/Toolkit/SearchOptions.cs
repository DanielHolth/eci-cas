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

    public int MaxResults { get; set; } = 4;

    /// <summary>Longest snippet kept per hit, cut at a word. The links carry the rest.</summary>
    public int SnippetChars { get; set; } = 160;

    /// <summary>Whether the best hit's page is fetched and its most relevant passages added. Local work only, no model.</summary>
    public bool ReadPage { get; set; } = true;

    public int ReadTimeoutSeconds { get; set; } = 10;

    /// <summary>Most bytes read off a page; the rest is never downloaded.</summary>
    public int PageBytes { get; set; } = 1_000_000;

    /// <summary>Target size of one page chunk, cut at sentences.</summary>
    public int ChunkChars { get; set; } = 500;

    /// <summary>Budget of whole chunks handed on from the page.</summary>
    public int ExtractChars { get; set; } = 1500;

    /// <summary>Longest query sent out; a spoken turn can ramble and the engine only needs the gist.</summary>
    public int MaxQueryChars { get; set; } = 200;

    public string BraveKeyEnvironmentVariable { get; set; } = "BRAVE_API_KEY";
}
