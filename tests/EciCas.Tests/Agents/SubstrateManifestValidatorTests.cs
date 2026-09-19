using System.Runtime.CompilerServices;
using EciCas.Agents.Utterances;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;

using EciCas.Substrates;

namespace EciCas.Tests.Agents;

public class SubstrateManifestValidatorTests
{
    private sealed class StubAgent(string name) : IAgent
    {
        public string Name => name;
        public IReadOnlyCollection<string> Subscriptions => [];
        public Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubCognitiveAgent(string name) : IAgent, ICognitiveAgent
    {
        public string Name => name;
        public IReadOnlyCollection<string> Subscriptions => [];
        public Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static SubstrateOptions Backing(string agent) =>
        new() { Agents = { [agent] = new SubstrateAgentEntry() } };

    [Fact]
    public void WhenTableMatchesCognitiveAgents_DoesNotThrow()
    {
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        var exception = Record.Exception(() => SubstrateManifestValidator.Validate(Backing("Intent"), agents));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenTableDeclaresUnregisteredAgent_Throws()
    {
        var agents = Array.Empty<IAgent>();

        Assert.Throws<InvalidOperationException>(() => SubstrateManifestValidator.Validate(Backing("Ghost"), agents));
    }

    [Fact]
    public void WhenCognitiveAgentIsUnbacked_Throws()
    {
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        Assert.Throws<InvalidOperationException>(() => SubstrateManifestValidator.Validate(new SubstrateOptions(), agents));
    }

    /// <summary>
    /// Deterministic by configuration is still an agent the tier has to name:
    /// UseSubstrate:false says which model it *would* have used, and dropping
    /// the entry hides that decision rather than recording it.
    /// </summary>
    [Fact]
    public void WhenUnbackedAgentIsDeterministic_StillThrows()
    {
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        Assert.Throws<InvalidOperationException>(() => SubstrateManifestValidator.Validate(new SubstrateOptions(), agents));
    }

    [Fact]
    public void WhenNonCognitiveAgentIsRegistered_IsIgnored()
    {
        var agents = new IAgent[] { new StubAgent("ArchiveLogger") };

        var exception = Record.Exception(() => SubstrateManifestValidator.Validate(new SubstrateOptions(), agents));
        Assert.Null(exception);
    }

    /// <summary>
    /// Every tier's table against the agents in the assembly, which is the
    /// one pairing the stub tests above cannot check. A tier is the live
    /// table -- appsettings.json's own Agents block is the mock default and
    /// deliberately partial -- so this is the check the host makes at boot,
    /// made once per preset instead of once for whichever tier was started.
    ///
    /// Sight shipped as an AgentBase with an entry in all five files and
    /// without the ICognitiveAgent marker, and every test passed while the
    /// host refused to boot: the marker is how this table finds an agent, so
    /// an unmarked one reads as a name backing nothing.
    /// </summary>
    [Fact]
    public void EveryTierBacksEveryCognitiveAgent()
    {
        var agents = new[] { typeof(EciCas.Agents.Sight.SightAgent).Assembly, typeof(AgentBase).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IAgent).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (IAgent)RuntimeHelpers.GetUninitializedObject(t))

            // Classes kept but no longer registered are not a tier's business;
            // boot catches a registered one a tier misses.
            .Where(a => a.Name != "Archivist")
            .ToList();

        var substrates = new SubstrateOptions();
        var catalog = new TierCatalog(TierCatalogLoader.Load(AppContext.BaseDirectory), substrates,
            new RuntimeKnobs(), new KnobDefaults(), "Mock");

        foreach (var preset in catalog.Presets)
        {
            Assert.True(catalog.Switch(preset.Name));
            SubstrateManifestValidator.Validate(substrates, agents,
                [SubstrateConsolidator.AgentName, SubstrateFactExtractor.AgentName, FactPicker.AgentName, MaintenanceOptions.RebuildAgentName, EciCas.Agents.Toolkit.SettingsCapability.AgentName, EciCas.Agents.Toolkit.ToolsmithCapability.AgentName]);
        }
    }
}
