using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Substrates;

namespace EciCas.Tests.Host;

/// <summary>
/// The tier files themselves are the fixture, for the same reason the agent
/// tests read the shipped instructions: a preset that only ever describes a
/// hand-written table proves the loader parses its own invention. What a
/// live switch has to survive is whatever Free actually says today.
/// </summary>
public class TierCatalogTests
{
    private static string TierDirectory => AppContext.BaseDirectory;

    private static (TierCatalog Catalog, SubstrateOptions Substrates, RuntimeKnobs Knobs) Build()
    {
        var substrates = new SubstrateOptions();
        var knobs = new RuntimeKnobs();
        var knobDefaults = new KnobDefaults();
        var catalog = new TierCatalog(TierCatalogLoader.Load(TierDirectory), substrates, knobs, knobDefaults, "Mock");
        return (catalog, substrates, knobs);
    }

    /// <summary>
    /// The agents a tier actually speaks with. The rebuild entry sits in the
    /// same table and is deliberately none of a tier's business -- it names a
    /// vendor model on Free too, because repairing the index is not the
    /// persona thinking. Every claim below is about the persona, so it says
    /// so rather than quietly counting a row it does not mean.
    /// </summary>
    private static IEnumerable<SubstrateAgentEntry> Persona(SubstrateOptions substrates) =>
        substrates.Agents.Where(a => a.Key != MaintenanceOptions.RebuildAgentName).Select(a => a.Value);

    [Fact]
    public void EveryShippedTierLoads() =>
        Assert.Contains(TierCatalogLoader.Load(TierDirectory), p => p.Name == "Free");

    /// <summary>
    /// Tiers have one axis -- Mock is the worst, Premium is the best -- and the
    /// dropdown is a dial along it. Ordering by file name put Budget above
    /// Free, which reads as a claim about cost that is not true.
    /// </summary>
    [Fact]
    public void TiersAreOrderedCheapestFirst_NotAlphabetically() =>
        Assert.Equal(["Mock", "Free", "Budget", "Pro", "Premium"],
            TierCatalogLoader.Load(TierDirectory).Select(p => p.Name));

    /// <summary>
    /// Only tier files are tiers. appsettings.json alone was briefly listed
    /// as "base", which was an implementation detail wearing a tier's name --
    /// nobody specified a sixth tier, and an unset --Tier now layers Mock
    /// rather than leaving that state reachable at all.
    /// </summary>
    [Fact]
    public void TheBareConfigurationIsNotATier() =>
        Assert.DoesNotContain(TierCatalogLoader.Load(TierDirectory),
            p => p.Name.Equals("base", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mock is the tier a host falls back to, so it has to be a destination
    /// like any other: a host that switched to Free and wants out needs
    /// somewhere free to land.
    /// </summary>
    [Fact]
    public void TheFreeTierIsSomewhereYouCanReturnTo()
    {
        var (catalog, substrates, _) = Build();

        Assert.True(catalog.Switch("Free"));
        Assert.True(catalog.Switch("Mock"));

        Assert.Equal("Mock", catalog.Active);
        Assert.All(Persona(substrates), c => Assert.Equal("mock", c.Provider));
    }

    /// <summary>
    /// A tier is not only its models. Free also shrinks the Recall
    /// fan-out and switches Reflection off entirely, and a switch that moved
    /// the substrate table alone would be a different tier wearing the
    /// name — the thing the roadmap warned about before this existed.
    /// </summary>
    [Fact]
    public void SwitchingCarriesTheWholeTier_NotJustItsModels()
    {
        var (catalog, substrates, knobs) = Build();

        Assert.True(catalog.Switch("Free"));

        Assert.Equal("Free", catalog.Active);
        Assert.All(Persona(substrates), c => Assert.Equal("local", c.Provider));
        Assert.False(substrates.Agents["Reflection"].UseSubstrate);
        Assert.True(substrates.Agents["Intent"].UseSubstrate);

        // Depth is asserted against the preset rather than against a
        // literal, because it is the value the Debug panel's Save button
        // writes back to the tier file: pinning it to a number here means
        // every legitimate save breaks this test with a failure that says
        // nothing about whether switching works.
        var minimal = catalog.Presets.Single(p => p.Name == "Free");

        // The live knob overrides its option, so leaving it behind would run
        // the new tier at the old one's fan-out.
        Assert.Equal(minimal.Knobs.RecallDepth, knobs.RecallDepth);
    }

    /// <summary>
    /// Every class table is replaced by reference rather than edited, which
    /// is what lets a fan-out already in flight read one coherent tier.
    /// Asserting the old dictionary is untouched is how that stays true.
    /// </summary>
    [Fact]
    public void SwitchingReplacesTheClassTable_RatherThanEditingIt()
    {
        var (catalog, substrates, _) = Build();
        catalog.Switch("Mock");
        var before = substrates.Agents;

        catalog.Switch("Free");

        Assert.NotSame(before, substrates.Agents);
        Assert.All(before.Where(a => a.Key != MaintenanceOptions.RebuildAgentName).Select(a => a.Value), c => Assert.Equal("mock", c.Provider));
    }

    [Fact]
    public void AnUnknownTierIsRefused_AndChangesNothing()
    {
        var (catalog, substrates, _) = Build();
        catalog.Switch("Free");

        Assert.False(catalog.Switch("Minmal"));

        Assert.Equal("Free", catalog.Active);
        Assert.All(Persona(substrates), c => Assert.Equal("local", c.Provider));
    }

    /// <summary>
    /// The rebuild survives a tier switch, and does not cost Free its
    /// selectability. Both halves have a way of going wrong quietly: a switch
    /// replaces the whole agent table by reference, so an entry installed once
    /// into the booted table would vanish the first time anyone moved the
    /// dropdown; and an entry installed before the missing-key check would
    /// have Free reporting a want of OPENAI_API_KEY and greyed out for it.
    /// </summary>
    [Fact]
    public void TheRebuildIsInEveryTier_AndCostsFreeNothing()
    {
        var (catalog, substrates, _) = Build();

        Assert.True(catalog.Switch("Free"));

        Assert.Equal("openai", substrates.Agents[MaintenanceOptions.RebuildAgentName].Provider);
        Assert.Empty(catalog.Presets.Single(p => p.Name == "Free").MissingKeys);
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
        Assert.True(Build().Catalog.Switch("Pro"));
    }
}
