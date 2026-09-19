using System.Net;
using EciCas.Agents.Toolkit;

namespace EciCas.Tests.Bus;

public sealed class ToolkitReadTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.5", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    public void ReaderGuard_BlocksPrivateAddresses(string ip, bool blocked) =>
        Assert.Equal(blocked, ReaderGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("ftp://example.com/a", false)]
    [InlineData("http://localhost/a", false)]
    [InlineData("http://192.168.1.1/a", false)]
    public void ReaderGuard_AllowsOnlyPublicHttp(string url, bool allowed) =>
        Assert.Equal(allowed, ReaderGuard.IsAllowedUrl(new Uri(url)));

    [Fact]
    public void HtmlToText_DropsScriptAndChromeKeepsArticle()
    {
        var text = PageReader.HtmlToText("<html><nav>MENU</nav><script>bad()</script><article><p>Hello &amp; welcome.</p><p>Second.</p></article></html>");
        Assert.Contains("Hello & welcome.", text);
        Assert.DoesNotContain("MENU", text);
        Assert.DoesNotContain("bad()", text);
    }

    [Fact]
    public void DuckDuckGo_SkipsSponsoredClickThroughs()
    {
        const string html = """
            <a class="result__a" href="//duckduckgo.com/y.js?ad_domain=x.com&u3=abc">Ad</a>
            <a class="result__snippet">ad text</a>
            <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fp">Real</a>
            <a class="result__snippet">real text</a>
            """;
        var hits = DuckDuckGoSearchProvider.Parse(html, 5);
        Assert.Single(hits);
        Assert.Equal("Real", hits[0].Title);
    }

    [Fact]
    public void ToolkitOptions_EmptyAllowsAll_ListRestricts()
    {
        Assert.True(new ToolkitOptions().Allows("powershell"));
        var budget = new ToolkitOptions { Allowed = ["search", "guide"] };
        Assert.True(budget.Allows("Search"));
        Assert.False(budget.Allows("powershell"));
    }
}
