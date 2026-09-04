using EciCas.Substrates;

namespace EciCas.Tests.Substrates;

public class MockSubstrateProviderTests
{
    private static async Task<string> Complete(string prompt) =>
        (await new MockSubstrateProvider().CompleteAsync("fast-high", prompt, CancellationToken.None)).Text;

    /// <summary>
    /// The mock's echo is the free tier's entire visible output, and Intent's
    /// last line carries the turn followed by the standing asides every turn
    /// carries. Echoing those back buried what was actually said under the
    /// same three brackets every time.
    /// </summary>
    [Fact]
    public async Task EchoesTheTurn_NotTheAsidesTrailingIt()
    {
        var text = await Complete(
            "standing rules\n\nReply to: what is a tide [Impulse: none] [Identity: You are warm.] [Recall: [{\"a\":1}]]");

        Assert.Equal("[mock:fast-high] Reply to: what is a tide", text);
    }

    [Fact]
    public async Task EchoesTheWholeLine_WhenNothingTrailsTheTurn()
    {
        Assert.Equal("[mock:fast-high] Reply to: what is a tide",
            await Complete("standing rules\n\nReply to: what is a tide"));
    }

    /// <summary>
    /// Librarian and Recall enumerate candidates and expect a bare index
    /// back. An echo parses to nothing there, so the knowledge swarm silently
    /// took its empty path on the free tier.
    /// </summary>
    [Fact]
    public async Task AnswersAnEnumeratedQuestionWithAnIndex()
    {
        Assert.Equal("0", await Complete("pick one:\n0. assistant/identity\n1. user/preferences"));
    }
}
