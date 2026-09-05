using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Substrates;

namespace EciCas.Tests.Host;

/// <summary>
/// The tier files themselves are the fixture, for the same reason the agent
/// tests read the shipped instructions: a preset that only ever describes a
/// hand-written table proves the loader parses its own invention. What a
/// live switch has to survive is whatever Minimal actually says today.
/// </summary>
public class TierCatalogTests
{
    private static string TierDirectory => AppContext.BaseDirectory;

    private static (TierCatalog Catalog, SubstrateOptions Substrates, AgentSubstrateManifest Agents,
        RecallOptions Recall, LibrarianOptions Librarian, RuntimeKnobs Knobs) Build()
    {
        var substrates = new SubstrateOptions();
        var agents = new AgentSubstrateManifest();
        var recall = new RecallOptions();
        var librarian = new LibrarianOptions();
        var knobs = new RuntimeKnobs();
        var catalog = new TierCatalog(TierCatalogLoader.Load(TierDirectory), substrates, agents, recall, librarian,
            knobs, TierCatalog.BaseTier);
        return (catalog, substrates, agents, recall, librarian, knobs);
    }

    [Fact]
    public void EveryShippedTierLoads() =>
        Assert.Contains(TierCatalogLoader.Load(TierDirectory), p => p.Name == "Minimal");

    /// <summary>
    /// Tiers have one axis -- Mock is the worst, Super is the best -- and the
    /// dropdown is a dial along it. Ordering by file name put Budget above
    /// Minimal, which reads as a claim about cost that is not true.
    /// </summary>
    [Fact]
    public void TiersAreOrderedCheapestFirst_NotAlphabetically() =>
        Assert.Equal(["base", "Mock", "Minimal", "Budget", "Default", "Super"],
            TierCatalogLoader.Load(TierDirectory).Select(p => p.Name));

    /// <summary>
    /// The base entry is what "no --Tier" means, and it has to be
    /// selectable: a host booted bare that switched to Minimal could
    /// otherwise never get back to where it started.
    /// </summary>
    [Fact]
    public void TheBaseConfigurationIsATierYouCanReturnTo()
    {
        var (catalog, substrates, _, recall, _, _) = Build();

        Assert.True(catalog.Switch("Minimal"));
        Assert.True(catalog.Switch(TierCatalog.BaseTier));

        Assert.Equal(TierCatalog.BaseTier, catalog.Active);
        Assert.All(substrates.Classes.Values, c => Assert.Equal("mock", c.Provider));
        Assert.Equal(50, recall.RowsPerWorker);
    }

    /// <summary>
    /// A tier is not only its models. Minimal also shrinks the Recall
    /// fan-out and switches Reflection off entirely, and a switch that moved
    /// the substrate table alone would be a different tier wearing the
    /// name — the thing the roadmap warned about before this existed.
    /// </summary>
    [Fact]
    public void SwitchingCarriesTheWholeTier_NotJustItsModels()
    {
        var (catalog, substrates, agents, recall, librarian, knobs) = Build();

        Assert.True(catalog.Switch("Minimal"));

        Assert.Equal("Minimal", catalog.Active);
        Assert.All(substrates.Classes.Values, c => Assert.Equal("local", c.Provider));
        Assert.False(agents.Agents["Reflection"].UseSubstrate);
        Assert.True(agents.Agents["Intent"].UseSubstrate);
        Assert.Equal(10, recall.RowsPerWorker);
        Assert.Equal(2, librarian.MaxSelectedPairs);

        // RecallDepth overrides MaxPickedPerWorker, so leaving it behind
        // would run the new tier at the old one's depth.
        Assert.Equal(2, knobs.RecallDepth);
    }

    /// <summary>
    /// Every class table is replaced by reference rather than edited, which
    /// is what lets a fan-out already in flight read one coherent tier.
    /// Asserting the old dictionary is untouched is how that stays true.
    /// </summary>
    [Fact]
    public void SwitchingReplacesTheClassTable_RatherThanEditingIt()
    {
        var (catalog, substrates, _, _, _, _) = Build();
        catalog.Switch(TierCatalog.BaseTier);
        var before = substrates.Classes;

        catalog.Switch("Minimal");

        Assert.NotSame(before, substrates.Classes);
        Assert.All(before.Values, c => Assert.Equal("mock", c.Provider));
    }

    [Fact]
    public void AnUnknownTierIsRefused_AndChangesNothing()
    {
        var (catalog, substrates, _, _, _, _) = Build();
        catalog.Switch("Minimal");

        Assert.False(catalog.Switch("Minmal"));

        Assert.Equal("Minimal", catalog.Active);
        Assert.All(substrates.Classes.Values, c => Assert.Equal("local", c.Provider));
    }

    /// <summary>
    /// Missing keys are reported, never enforced. The surface greys those
    /// tiers out, but an operator who points at one is entitled to read the
    /// failure rather than be told no by the catalog.
    /// </summary>
    [Fact]
    public void ATierWhoseKeysAreMissingIsStillSelectable()
    {
        var presets = TierCatalogLoader.Load(TierDirectory);
        var mock = presets.Single(p => p.Name == "Mock");

        Assert.Empty(mock.MissingKeys);
        Assert.True(Build().Catalog.Switch("Default"));
    }
}
