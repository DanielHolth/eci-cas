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
    public void A_capability_above_the_tiers_risk_ceiling_is_unavailable_and_unapproved_is_pending()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(folder, "ps.json"), """{"name":"ps","description":"d","triggers":["t"],"verb":{"capability":"speak_text"},"approved":true}""");
        File.WriteAllText(Path.Combine(folder, "hook.json"), """{"name":"hook","description":"d","triggers":["t"],"verb":{"capability":"http_call","options":{"url":"https://example.com/x"}},"approved":true}""");
        File.WriteAllText(Path.Combine(folder, "draft.json"), """{"name":"draft","description":"d","triggers":["t"],"verb":{"capability":"speak_text"}}""");

        using var catalog = new ManifestCatalog([new ManifestSource(folder)], "Budget",
            new ToolkitOptions { MaxRisk = CapabilityRisk.Local },
            [new SpeakTextCapability(), new HttpCallCapability(null!)], _ => { }, watch: false);

        Assert.Equal(["ps"], catalog.All.Select(d => d.Name));
        Assert.Equal(ManifestStatus.Unavailable, catalog.Entries.Single(e => e.File == "hook.json").Status);
        Assert.Equal(ManifestStatus.Pending, catalog.Entries.Single(e => e.File == "draft.json").Status);
        Directory.Delete(folder, true);
    }
}
