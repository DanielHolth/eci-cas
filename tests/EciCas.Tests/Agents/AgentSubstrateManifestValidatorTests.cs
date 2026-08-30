using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Substrates;

namespace EciCas.Tests.Agents;

public class AgentSubstrateManifestValidatorTests
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

    private static SubstrateOptions OptionsWithClass(string className) =>
        new() { Classes = { [className] = new SubstrateClassEntry() } };

    [Fact]
    public void WhenManifestMatchesCognitiveAgents_DoesNotThrow()
    {
        var manifest = new AgentSubstrateManifest { Agents = { ["Intent"] = "fast-medium" } };
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        var exception = Record.Exception(() => AgentSubstrateManifestValidator.Validate(manifest, OptionsWithClass("fast-medium"), agents));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenManifestDeclaresUnregisteredAgent_Throws()
    {
        var manifest = new AgentSubstrateManifest { Agents = { ["Ghost"] = "fast-medium" } };
        var agents = Array.Empty<IAgent>();

        Assert.Throws<InvalidOperationException>(() => AgentSubstrateManifestValidator.Validate(manifest, OptionsWithClass("fast-medium"), agents));
    }

    [Fact]
    public void WhenCognitiveAgentMissingFromManifest_Throws()
    {
        var manifest = new AgentSubstrateManifest();
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        Assert.Throws<InvalidOperationException>(() => AgentSubstrateManifestValidator.Validate(manifest, new SubstrateOptions(), agents));
    }

    [Fact]
    public void WhenSubstrateClassIsUnknown_Throws()
    {
        var manifest = new AgentSubstrateManifest { Agents = { ["Intent"] = "typo-class" } };
        var agents = new IAgent[] { new StubCognitiveAgent("Intent") };

        Assert.Throws<InvalidOperationException>(() => AgentSubstrateManifestValidator.Validate(manifest, OptionsWithClass("fast-medium"), agents));
    }

    [Fact]
    public void WhenNonCognitiveAgentIsRegistered_IsIgnored()
    {
        var manifest = new AgentSubstrateManifest();
        var agents = new IAgent[] { new StubAgent("ArchiveLogger") };

        var exception = Record.Exception(() => AgentSubstrateManifestValidator.Validate(manifest, new SubstrateOptions(), agents));
        Assert.Null(exception);
    }
}
