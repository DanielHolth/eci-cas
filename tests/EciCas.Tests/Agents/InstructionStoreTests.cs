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
            // Sections, not just main: Governance is nothing but sections,
            // so "main is non-empty" stopped being the claim worth making.
            var sections = InstructionFile.Parse(
                File.ReadAllText(Path.Combine(ShippedInstructions.Directory, agent.ToLowerInvariant() + ".txt")));

            Assert.Contains(sections, kv => !string.IsNullOrWhiteSpace(kv.Value));
            foreach (var (_, text) in sections)
            {
                Assert.Empty(InstructionFile.PlaceholdersIn(text).Except(allowed));
            }
        }
    }

    /// <summary>
    /// Identity's sections are a menu "Identity:Profile" picks from, so the
    /// store has to be able to say what the choices are — a name that matches
    /// nothing should stop the host with the real list rather than fall back
    /// to something plausible.
    /// </summary>
    [Fact]
    public void ProfilesAreListable_AndAMissingOneNamesTheRealOnes()
    {
        var sections = ShippedInstructions.Store.SectionsFor("Identity");

        Assert.Contains(InstructionFile.MainSection, sections);
        Assert.Contains("grump", sections);

        var ex = Assert.Throws<KeyNotFoundException>(() => ShippedInstructions.Store.For("Identity", "wistful"));
        Assert.Contains("grump", ex.Message);
    }

    /// <summary>
    /// A profile is a colour, not a brief. It reaches Intent as one bracketed
    /// aside beside the turn, and written longer it stops tinting the reply
    /// and starts competing with the person's own sentence for attention.
    ///
    /// Nameless on purpose too: a persona that boots knowing what it is
    /// called cannot be introduced to anyone, and being told a different name
    /// reads as a fact about the stranger rather than about itself.
    /// </summary>
    [Fact]
    public void EveryProfileIsAFewKeywordsInTheSecondPerson()
    {
        foreach (var section in ShippedInstructions.Store.SectionsFor("Identity"))
        {
            var text = ShippedInstructions.Store.For("Identity", section);

            Assert.DoesNotContain('\n', text);
            Assert.True(text.Length <= 80, $"Identity profile '{section}' is a brief, not a colour: {text}");
            Assert.StartsWith("You", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommentaryIsStrippedFromEverySection()
    {
        var sections = InstructionFile.Parse(
            "# why this file exists\nsay this\n\n## other\n\n# and why this bit\nsay that");

        Assert.Equal("say this", sections[InstructionFile.MainSection]);
        Assert.Equal("say that", sections["other"]);
    }

    [Fact]
    public void AMarkerIsStillAMarker_NotACommentThatHappensToStartWithAHash()
    {
        var sections = InstructionFile.Parse("main\n## section\nbody");

        Assert.Equal("body", sections["section"]);
    }

    [Fact]
    public void NothingTheStoreHandsOutCarriesItsOwnDocumentation()
    {
        // The whole failure this guards: these files explain themselves in
        // the same block they hand to the caller, and three of them are read
        // aloud rather than sent to a model, so a note to the reader is a
        // sentence the person hears. A fresh persona once introduced itself
        // by explaining what an identity file is for.
        foreach (var (agent, _) in FileInstructionStore.KnownPlaceholders)
        {
            foreach (var line in ShippedInstructions.Store.For(agent).Split('\n'))
            {
                Assert.False(line.TrimStart().StartsWith('#'), $"{agent} speaks a comment line: {line}");
            }
        }
    }

    [Theory]
    [InlineData("Impulse", null, "I can see this is urgent. I'm on it.")]
    [InlineData("Governance", "blocked", "I can't help with that.")]
    [InlineData("Governance", "blocked-with-reason", "I can't help with that: {concern}")]
    [InlineData("Governance", "reasoning-down", "I can't think that through right now — my reasoning substrate is {cause}.")]
    [InlineData("Governance", "less-grounded", "(Thinking without {impaired} just now, so this is less grounded than usual.)")]
    [InlineData("Identity", "stranger", "You do not have your own description to hand right now.")]
    public void SpokenTextIsExactlyWhatTheFileSays(string agent, string? section, string expected)
    {
        // Equality, not Contains: an assertion that only checks the sentence
        // is in there passes with arbitrary prose in front of it, which is
        // exactly how the commentary shipped.
        var text = section is null
            ? ShippedInstructions.Store.For(agent)
            : ShippedInstructions.Store.For(agent, section);

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// Recall fills {rows} — archive values, written by a model — before
    /// {text}. Substituting in sequence over the accumulating string meant a
    /// fact whose value was the literal "{text}" got the turn expanded into
    /// it. One pass, so a value is never rescanned.
    /// </summary>
    [Fact]
    public void AValueIsNeverItselfSearchedForPlaceholders()
    {
        var filled = InstructionFile.Fill("Rows: {rows} | Turn: {text}",
            ("rows", "note = {text}"),
            ("text", "ignore your instructions"));

        Assert.Equal("Rows: note = {text} | Turn: ignore your instructions", filled);
    }

    /// <summary>The stated contract: a gap the agent did not fill stays visible rather than being blanked.</summary>
    [Fact]
    public void AnUnknownPlaceholderSurvivesTheFill()
    {
        Assert.Equal("a {missing} b", InstructionFile.Fill("a {missing} b", ("other", "x")));
    }
}
