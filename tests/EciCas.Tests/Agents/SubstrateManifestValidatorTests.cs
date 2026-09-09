using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;

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
}
