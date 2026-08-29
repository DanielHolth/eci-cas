using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class RoutingManifestTests
{
    private sealed class StubAgent(string name, params string[] subscriptions) : IAgent
    {
        public string Name => name;
        public IReadOnlyCollection<string> Subscriptions => subscriptions;
        public Task HandleAsync(Envelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void WhenManifestMatchesRegisteredAgents_DoesNotThrow()
    {
        var manifest = new RoutingManifest
        {
            Agents = new()
            {
                ["A"] = new ManifestAgentEntry { Subscribes = ["events.perception"] },
            },
        };
        var agents = new IAgent[] { new StubAgent("A", "events.perception") };

        var exception = Record.Exception(() => RoutingManifest.Validate(manifest, agents));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenAgentDeclarationsDrift_FailsAtStartup()
    {
        var manifest = new RoutingManifest
        {
            Agents = new()
            {
                ["A"] = new ManifestAgentEntry { Subscribes = ["events.perception"] },
            },
        };
        var agents = new IAgent[] { new StubAgent("A", "events.perception", "events.advisories") };

        Assert.Throws<InvalidOperationException>(() => RoutingManifest.Validate(manifest, agents));
    }

    [Fact]
    public void WhenAgentMissingFromManifest_FailsAtStartup()
    {
        var manifest = new RoutingManifest { Agents = new() };
        var agents = new IAgent[] { new StubAgent("A", "events.perception") };

        Assert.Throws<InvalidOperationException>(() => RoutingManifest.Validate(manifest, agents));
    }
}
