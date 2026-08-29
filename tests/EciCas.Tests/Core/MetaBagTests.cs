using EciCas.Core;

namespace EciCas.Tests.Core;

public class MetaBagTests
{
    [Fact]
    public void Merge_OverlaysOtherKeys_AndKeepsOwnUniqueKeys()
    {
        var a = MetaBag.Empty.With("shared", "from-a").With("only-a", 1);
        var b = MetaBag.Empty.With("shared", "from-b").With("only-b", 2);

        var merged = a.Merge(b);

        Assert.Equal("from-b", merged.Get<string>("shared"));
        Assert.Equal(1, merged.Get<int>("only-a"));
        Assert.Equal(2, merged.Get<int>("only-b"));
    }
}
