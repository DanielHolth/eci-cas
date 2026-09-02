using EciCas.Core;

namespace EciCas.Tests.Agents;

public class InstructionStoreTests
{
    [Fact]
    public void MainSectionIsEverythingBeforeTheFirstMarker()
    {
        var sections = InstructionFile.Parse("do the thing\n\n## revisit\n\nand again");

        Assert.Equal("do the thing", sections[InstructionFile.MainSection]);
        Assert.Equal("and again", sections["revisit"]);
    }

    [Fact]
    public void AFileWithNoMarkersIsAllMain()
    {
        var sections = InstructionFile.Parse("just prose");

        Assert.Single(sections);
        Assert.Equal("just prose", sections[InstructionFile.MainSection]);
    }

    [Fact]
    public void PlaceholdersIgnoreBracesThatAreNotNames()
    {
        var found = InstructionFile.PlaceholdersIn("respond as {rows} — not as {a, b} or {}");

        Assert.Equal(["rows"], found);
    }

    /// <summary>
    /// The gap a hand revision leaves is left visible rather than blanked.
    /// An unfilled {turns} in the prompt is a bug someone can see; an empty
    /// string reads as the model having been told nothing, which is what a
    /// substrate would then faithfully answer.
    /// </summary>
    [Fact]
    public void UnknownPlaceholdersSurviveFilling()
    {
        Assert.Equal("a X {b}", InstructionFile.Fill("a {x} {b}", ("x", "X")));
    }

    [Fact]
    public void AMissingFileIsAStartupFailure()
    {
        var empty = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(empty);

        var ex = Assert.Throws<FileNotFoundException>(() => new FileInstructionStore(empty));
        Assert.Contains("instruction file", ex.Message);
    }

    /// <summary>
    /// The check that makes hand revision safe: {turn} instead of {turns}
    /// would otherwise reach the substrate as the literal word, and the only
    /// symptom would be worse notes.
    /// </summary>
    [Fact]
    public void APlaceholderNoAgentFillsIsAStartupFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        foreach (var (agent, _) in FileInstructionStore.KnownPlaceholders)
        {
            File.WriteAllText(Path.Combine(directory, agent.ToLowerInvariant() + ".txt"), "fine");
        }

        File.WriteAllText(Path.Combine(directory, "reflection.txt"), "read the {turn} closely");

        var ex = Assert.Throws<InvalidOperationException>(() => new FileInstructionStore(directory));
        Assert.Contains("{turn}", ex.Message);
        Assert.Contains("{turns}", ex.Message);
    }

    [Fact]
    public void EveryShippedFileLoadsAndNamesOnlyPlaceholdersItsAgentFills()
    {
        foreach (var (agent, allowed) in FileInstructionStore.KnownPlaceholders)
        {
            var text = ShippedInstructions.Store.For(agent);

            Assert.False(string.IsNullOrWhiteSpace(text), $"{agent} has an empty instruction");
            Assert.Empty(InstructionFile.PlaceholdersIn(text).Except(allowed));
        }
    }
}
