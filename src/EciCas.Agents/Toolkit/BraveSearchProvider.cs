using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>Brave Search API. The key comes from the environment variable named in <see cref="SearchOptions.BraveKeyEnvironmentVariable"/>, never from a file.</summary>
public sealed class BraveSearchProvider(IHttpClientFactory httpClientFactory, IOptions<SearchOptions> options) : ISearchProvider
{
    public string Name => "brave";

    public async Task<IReadOnlyList<ToolkitReference>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable(options.Value.BraveKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SearchUnavailableException($"{options.Value.BraveKeyEnvironmentVariable} is not set.");
        }

        var http = httpClientFactory.CreateClient("search");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?count={maxResults}&q={Uri.EscapeDataString(query)}");
        request.Headers.Add("X-Subscription-Token", key);
        request.Headers.Add("Accept", "application/json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SearchUnavailableException("Brave's quota is used up.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SearchUnavailableException($"Brave replied {(int)response.StatusCode}.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var hits = new List<ToolkitReference>();
        if (document.RootElement.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray().Take(maxResults))
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? url : url;
                var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                hits.Add(new ToolkitReference(title, url, WebUtility.HtmlDecode(Regex.Replace(description, "<[^>]+>", string.Empty))));
            }
        }

        return hits;
    }
}
