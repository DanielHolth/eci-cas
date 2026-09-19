using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Looks something up on the web. Hands back a few terse snippets (no URLs --
/// those go to the References log as <see cref="ToolkitReference"/>), plus
/// the most relevant passages of the best-matching hit's page, read and
/// ranked locally by embedding. No model call anywhere in here.
/// </summary>
public sealed class WebSearchCapability(
    IEnumerable<ISearchProvider> providers,
    IOptions<SearchOptions> options,
    IPageReader reader,
    IEmbeddingProvider embeddings) : ICapability
{
    /// <param name="MaxResults">Overrides the tier's <see cref="SearchOptions.MaxResults"/>.</param>
    /// <param name="QueryTemplate">Wraps the person's words, e.g. <c>"site:wikipedia.org {command}"</c>.</param>
    public sealed record Options(int? MaxResults = null, string? QueryTemplate = null);

    public string Name => "web_search";

    public string Description => "Searches the web for the person's words and reads the best page. Options: maxResults, queryTemplate containing {command}.";

    public CapabilityRisk Risk => CapabilityRisk.Network;

    public Type? OptionsType => typeof(Options);

    public string? Validate(object? options) =>
        options is Options { QueryTemplate: { } template } && !template.Contains("{command}") ? "queryTemplate must contain {command}" : null;

    public async Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var own = call.Options as Options ?? new Options();
        var command = own.QueryTemplate?.Replace("{command}", call.Command.Trim()) ?? call.Command;
        if (!settings.Enabled)
        {
            return new ToolkitOutcome(string.Empty, false, "Web search is turned off.");
        }

        var provider = providers.FirstOrDefault(p => p.Name.Equals(settings.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new ToolkitOutcome(string.Empty, false, $"No search provider named '{settings.Provider}'.");
        }

        var query = command.Trim();
        if (query.Length == 0)
        {
            return new ToolkitOutcome(string.Empty, false, "Nothing to search for.");
        }

        if (query.Length > settings.MaxQueryChars)
        {
            query = query[..settings.MaxQueryChars];
        }

        try
        {
            var hits = await provider.SearchAsync(query, own.MaxResults ?? settings.MaxResults, cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                return new ToolkitOutcome("The search found nothing.", true, null);
            }

            var lines = hits.Select((h, i) => $"{i + 1}. {h.Title} -- {Trim(h.Summary, settings.SnippetChars)}");
            var text = $"Web search for \"{query}\" found:" + Environment.NewLine + string.Join(Environment.NewLine, lines);

            if (settings.ReadPage)
            {
                var hot = await PickHotHitAsync(query, hits, cancellationToken).ConfigureAwait(false);
                var extract = await reader.ExtractAsync(hot.Url, query, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extract))
                {
                    text += Environment.NewLine + Environment.NewLine + $"From the page \"{hot.Title}\":" + Environment.NewLine + extract;
                }
            }

            return new ToolkitOutcome(text, true, null, hits);
        }
        catch (SearchUnavailableException unavailable)
        {
            return new ToolkitOutcome(string.Empty, false, $"Search is unavailable right now: {unavailable.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't reach the search engine: {failure.Message}");
        }
    }

    /// <summary>The hit whose title and snippet sit closest to the question; rank order if embeddings are down.</summary>
    private async Task<ToolkitReference> PickHotHitAsync(string query, IReadOnlyList<ToolkitReference> hits, CancellationToken cancellationToken)
    {
        if (hits.Count == 1)
        {
            return hits[0];
        }

        try
        {
            var q = (await embeddings.EmbedAsync([query], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false))[0];
            var docs = await embeddings.EmbedAsync([.. hits.Select(h => $"{h.Title}. {h.Summary}")], EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
            if (q.Length == 0 || docs.Count != hits.Count)
            {
                return hits[0];
            }

            var best = 0;
            var bestScore = double.MinValue;
            for (var i = 0; i < docs.Count; i++)
            {
                double dot = 0;
                var n = Math.Min(q.Length, docs[i].Length);
                for (var k = 0; k < n; k++)
                {
                    dot += q[k] * docs[i][k];
                }

                if (dot > bestScore)
                {
                    bestScore = dot;
                    best = i;
                }
            }

            return hits[best];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return hits[0];
        }
    }

    /// <summary>Cuts at a word, not mid-word; the ellipsis says it was cut.</summary>
    private static string Trim(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }
}
