using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Looks something up on the web and hands back snippets and links. Discovery
/// only: it never fetches a page, so what Morrow can say is what the engine's
/// own snippets say, and each hit is also returned as a
/// <see cref="ToolkitReference"/> for the References log. Reading a page in
/// full is a separate toolkit.
/// </summary>
public sealed class SearchToolkit(IEnumerable<ISearchProvider> providers, IOptions<SearchOptions> options) : IToolkit
{
    public string Name => "search";

    public async Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
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
            var hits = await provider.SearchAsync(query, settings.MaxResults, cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                return new ToolkitOutcome("The search found nothing.", true, null);
            }

            var text = $"Web search for \"{query}\" found:" + Environment.NewLine +
                string.Join(Environment.NewLine, hits.Select((h, i) => $"{i + 1}. {h.Title} ({h.Url}) -- {h.Summary}"));
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
}
