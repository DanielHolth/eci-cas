using EciCas.Core;

namespace EciCas.Tests.Core;

public class PromptCapTests
{
    [Fact]
    public void Apply_WithMultiLineText_FlattensItOntoOneLine()
    {
        Assert.Equal("a companion mind, warm and unhurried",
            PromptCap.Apply("a companion mind,\nwarm and\n   unhurried"));
    }

    /// <summary>
    /// The cap counts what the prompt will carry, not the shape of the file
    /// the text was written in — otherwise a hard-wrapped, indented paragraph
    /// spends part of its budget on its own line breaks.
    /// </summary>
    [Fact]
    public void Apply_WhenFlatteningBringsItUnderTheCap_KeepsAllOfIt()
    {
        var indented = string.Join("\n    ", Enumerable.Repeat("0123456789", 12));

        var capped = PromptCap.Apply(indented, maxChars: 140);

        Assert.Equal(175, indented.Length);
        Assert.Equal(131, capped.Length);
        Assert.DoesNotContain("…", capped);
    }

    [Fact]
    public void Apply_BeyondTheCap_TruncatesWithAnEllipsis()
    {
        var capped = PromptCap.Apply(new string('x', 300));

        Assert.Equal(PromptCap.DefaultMaxChars + 1, capped.Length);
        Assert.EndsWith("…", capped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Apply_WithNothingToSay_ReturnsEmpty(string? text)
    {
        Assert.Equal(string.Empty, PromptCap.Apply(text));
    }
}
