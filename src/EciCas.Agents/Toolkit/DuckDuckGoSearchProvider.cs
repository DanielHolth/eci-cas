using System.Net;
using System.Text.RegularExpressions;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Keyless search through DuckDuckGo's HTML endpoint. Scraping, so it breaks
/// when the markup changes and answers with a challenge page under bursts --
/// both surface as <see cref="SearchUnavailableException"/> rather than as an
/// empty result that looks like "nothing found".
/// </summary>
public sealed partial class DuckDuckGoSearchProvider(IHttpClientFactory httpClientFactory) : ISearchProvider
{
    public string Name => "duckduckgo";

    public async Task<IReadOnlyList<ToolkitReference>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("search");
        using var response = await http.PostAsync(
            "https://html.duckduckgo.com/html/",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query }),
            cancellationToken).ConfigureAwait(false);

        // DDG answers 202 to a request it wants a human for.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests || !response.IsSuccessStatusCode)
        {
            throw new SearchUnavailableException($"DuckDuckGo replied {(int)response.StatusCode}.");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hits = Parse(html, maxResults);
        if (hits.Count == 0 && html.Contains("anomaly", StringComparison.OrdinalIgnoreCase))
        {
            throw new SearchUnavailableException("DuckDuckGo asked for a challenge.");
        }

        return hits;
    }

    internal static IReadOnlyList<ToolkitReference> Parse(string html, int maxResults)
    {
        var links = LinkPattern().Matches(html);
        var snippets = SnippetPattern().Matches(html);
        var hits = new List<ToolkitReference>();

        for (var i = 0; i < links.Count && hits.Count < maxResults; i++)
        {
            var url = Unwrap(WebUtility.HtmlDecode(links[i].Groups[1].Value));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var title = Clean(links[i].Groups[2].Value);
            var summary = i < snippets.Count ? Clean(snippets[i].Groups[1].Value) : string.Empty;
            hits.Add(new ToolkitReference(title, uri.ToString(), summary));
        }

        return hits;
    }

    /// <summary>Result links come wrapped as //duckduckgo.com/l/?uddg=&lt;encoded target&gt;.</summary>
    private static string Unwrap(string href)
    {
        var start = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (start < 0)
        {
            return href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        }

        var end = href.IndexOf('&', start);
        var encoded = end < 0 ? href[(start + 5)..] : href[(start + 5)..end];
        return Uri.UnescapeDataString(encoded);
    }

    private static string Clean(string fragment) =>
        WebUtility.HtmlDecode(TagPattern().Replace(fragment, string.Empty)).Trim();

    [GeneratedRegex("""class="result__a"[^>]*href="([^"]+)"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("""class="result__snippet"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();
}
