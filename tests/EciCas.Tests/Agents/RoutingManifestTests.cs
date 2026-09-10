using System.Runtime.CompilerServices;
using EciCas.Agents.Utterances;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// The shipped manifest against every real agent class. Each agent's
    /// Name and Subscriptions are read off an uninitialised instance, so
    /// an agent that changes its subscriptions fails here rather than at boot.
    /// </summary>
    [Fact]
    public void TheShippedManifestMatchesEveryAgent()
    {
        var manifest = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build().GetSection("RoutingManifest").Get<RoutingManifest>()!;

        var agents = new[] { typeof(ScribeAgent).Assembly, typeof(AgentBase).Assembly, typeof(RoutingManifest).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IAgent).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (IAgent)RuntimeHelpers.GetUninitializedObject(t))

            // Classes kept but no longer registered (Archivist) are not the
            // manifest's business; boot catches a registered one it misses.
            .Where(a => manifest.Agents.ContainsKey(a.Name))
            .ToList();

        RoutingManifest.Validate(manifest, agents);
    }

    [Fact]
    public void WhenAgentMissingFromManifest_FailsAtStartup()
    {
        var manifest = new RoutingManifest { Agents = new() };
        var agents = new IAgent[] { new StubAgent("A", "events.perception") };

        Assert.Throws<InvalidOperationException>(() => RoutingManifest.Validate(manifest, agents));
    }
}
