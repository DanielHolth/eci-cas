using EciCas.Agents.Toolkit;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Bus;

public class SearchToolkitTests
{
    private sealed class FakeProvider(Func<IReadOnlyList<ToolkitReference>> hits) : ISearchProvider
    {
        public string Name => "duckduckgo";
        public Task<IReadOnlyList<ToolkitReference>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult(hits());
    }

    [Fact]
    public void DuckDuckGo_parse_unwraps_redirect_links_and_pairs_snippets()
    {
        const string html = """
            <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1&amp;rut=abc">Example <b>A</b></a>
            <a class="result__snippet" href="x">First &amp; best snippet</a>
            <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fb">Example B</a>
            <a class="result__snippet" href="x">Second snippet</a>
            """;

        var hits = DuckDuckGoSearchProvider.Parse(html, 5);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://example.com/a?x=1", hits[0].Url);
        Assert.Equal("Example A", hits[0].Title);
        Assert.Equal("First & best snippet", hits[0].Summary);
        Assert.Equal("https://example.org/b", hits[1].Url);
    }

    [Fact]
    public async Task Hits_come_back_as_prose_and_as_references()
    {
        var hit = new ToolkitReference("Title", "https://example.com", "What it says");
        var toolkit = new SearchToolkit([new FakeProvider(() => [hit])], Options.Create(new SearchOptions()));

        var outcome = await toolkit.ExecuteAsync("latest news", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("https://example.com", outcome.Output);
        Assert.Equal([hit], outcome.References);
    }

    [Fact]
    public async Task A_refusing_engine_is_a_failure_not_an_empty_result()
    {
        var toolkit = new SearchToolkit(
            [new FakeProvider(() => throw new SearchUnavailableException("challenge"))],
            Options.Create(new SearchOptions()));

        var outcome = await toolkit.ExecuteAsync("anything", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("unavailable", outcome.Error);
    }

    [Fact]
    public async Task Turned_off_never_reaches_the_provider()
    {
        var called = false;
        var toolkit = new SearchToolkit(
            [new FakeProvider(() => { called = true; return []; })],
            Options.Create(new SearchOptions { Enabled = false }));

        var outcome = await toolkit.ExecuteAsync("anything", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(called);
    }
}
