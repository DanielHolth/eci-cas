namespace EciCas.Agents.Toolkit;

/// <summary>One web search engine behind <see cref="WebSearchCapability"/>; selected by <see cref="SearchOptions.Provider"/>.</summary>
public interface ISearchProvider
{
    /// <summary>Matched case-insensitively against <see cref="SearchOptions.Provider"/>.</summary>
    string Name { get; }

    /// <summary>Throws <see cref="SearchUnavailableException"/> when the engine refuses or rate-limits, so the toolkit can say so plainly.</summary>
    Task<IReadOnlyList<ToolkitReference>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);
}

public sealed class SearchUnavailableException(string message) : Exception(message);
