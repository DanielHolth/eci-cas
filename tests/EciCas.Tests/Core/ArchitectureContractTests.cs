using EciCas.Core;
using EciCas.Host.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Tests.Core;

public class ArchitectureContractTests
{
    [Fact]
    public void ProductDirectionNamesTheStrategicContract()
    {
        Assert.Equal("Thick client on the user's machine", ArchitectureContract.ProductDirection.ClientRuntime);
        Assert.Equal("Cross-platform host runtime across Windows, macOS and Linux", ArchitectureContract.ProductDirection.HostRuntime);
        Assert.Equal("Remote relay owns API keys and provider secrets", ArchitectureContract.ProductDirection.SecretsLocation);
        Assert.Equal("Steam Cloud owns durable user state and the utterance parquet archive", ArchitectureContract.ProductDirection.DurableState);
        Assert.Equal("Client-side local state remains device-scoped and transient", ArchitectureContract.ProductDirection.LocalState);
    }

    [Fact]
    public void RequiredLayersMatchTheExplicitSplitLayerGate()
    {
        Assert.Equal(
            [
                ArchitectureLayer.SharedCore,
                ArchitectureLayer.PlatformShell,
                ArchitectureLayer.RemoteRelay,
                ArchitectureLayer.SyncLayer,
            ],
            ArchitectureContract.RequiredLayers);
    }

    [Fact]
    public void ResponsibilitiesStaySeparatedByLayer()
    {
        Assert.Contains("bus", ArchitectureContract.ResponsibilityOf(ArchitectureLayer.SharedCore), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window", ArchitectureContract.ResponsibilityOf(ArchitectureLayer.PlatformShell), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", ArchitectureContract.ResponsibilityOf(ArchitectureLayer.RemoteRelay), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steam Cloud", ArchitectureContract.ResponsibilityOf(ArchitectureLayer.SyncLayer), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundaryValidationRejectsMissingLayers()
    {
        var missing = ArchitectureContract.Validate(new[] { ArchitectureLayer.SharedCore, ArchitectureLayer.PlatformShell });

        Assert.Equal([ArchitectureLayer.RemoteRelay, ArchitectureLayer.SyncLayer], missing);
    }

    [Fact]
    public void LayerRulesExpressAllowedDependencies_WithoutHardBlockingTheHost()
    {
        var rule = ArchitectureContract.RuleFor(ArchitectureLayer.PlatformShell);

        Assert.Contains(ArchitectureLayer.SharedCore, rule.AllowedDependencies);
        Assert.DoesNotContain(ArchitectureLayer.RemoteRelay, rule.AllowedDependencies);

        var findings = ArchitectureContract.ValidateDependencies(ArchitectureLayer.PlatformShell, [ArchitectureLayer.SharedCore, ArchitectureLayer.RemoteRelay]);

        Assert.Contains(findings, f => f.Contains("RemoteRelay", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(findings, f => f.Contains("hard block", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LayerBoundariesExistAsConcreteServices()
    {
        var shared = new SharedCoreBoundary();
        var shell = new PlatformShellBoundary();
        var relay = new RemoteRelayBoundary();
        var sync = new SyncLayerBoundary();

        Assert.Equal(ArchitectureLayer.SharedCore, shared.Layer);
        Assert.Equal(ArchitectureLayer.PlatformShell, shell.Layer);
        Assert.Equal(ArchitectureLayer.RemoteRelay, relay.Layer);
        Assert.Equal(ArchitectureLayer.SyncLayer, sync.Layer);

        Assert.Contains("bus", shared.Responsibility, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window", shell.Responsibility, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", relay.Responsibility, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steam Cloud", sync.Responsibility, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SharedCoreReliabilityScoresFacts_WithoutClutteringPayloads()
    {
        var scorer = new FactReliabilityScorer();
        var fact = new Fact(
            Id: "f-1",
            Turn: 7,
            Text: "My daughter's birthday is 14 April.",
            Timestamp: DateTimeOffset.UtcNow.AddDays(-30),
            Speaker: "user",
            Keywords: ["birthday", "daughter"],
            Class: FactClasses.Relation,
            Entity: "daughter",
            Confidence: null,
            Freshness: null);

        var result = await scorer.ScoreAsync(fact, CancellationToken.None);

        Assert.InRange(result.Confidence, 0d, 1d);
        Assert.InRange(result.Freshness, 0d, 1d);
        Assert.Equal("f-1", result.FactId);
        Assert.True(result.Confidence > 0.5d);
    }

    [Fact]
    public void LayerContractsAreRegisteredInTheContainer()
    {
        var services = new ServiceCollection();
        services.AddArchitectureBoundaries();

        Assert.Contains(services, s => s.ServiceType == typeof(IArchitectureBoundary));
        Assert.Contains(services, s => s.ServiceType == typeof(IFactReliabilityScorer));
        Assert.Contains(services, s => s.ServiceType == typeof(IToolRegistry));
    }

    [Fact]
    public void ArchitectureReviewServiceReportsFindings_WithoutBlockingStartup()
    {
        var service = new ArchitectureReviewService(
        [
            new SharedCoreBoundary(),
            new PlatformShellBoundary(),
            new RemoteRelayBoundary(),
            new SyncLayerBoundary(),
        ]);

        var review = service.Review();

        Assert.Empty(review.MissingLayers);
        Assert.True(review.HasFindings);
        Assert.Contains("PlatformShell depends on RemoteRelay", review.Findings[0]);
    }

    [Fact]
    public async Task ReliabilityScorerIsAppliedWhenFactsAreWritten()
    {
        var fact = new Fact(
            Id: "f-annotated",
            Turn: 11,
            Text: "The project uses a shared core and explicit layers.",
            Timestamp: DateTimeOffset.UtcNow.AddDays(-7),
            Speaker: "user",
            Keywords: ["project", "layers"],
            Class: FactClasses.Relation,
            Entity: "project",
            Confidence: null,
            Freshness: null);

        var annotated = await FactReliabilityAugmenter.ApplyAsync([fact], new FactReliabilityScorer(), CancellationToken.None);

        Assert.NotNull(annotated[0].Confidence);
        Assert.NotNull(annotated[0].Freshness);
        Assert.InRange(annotated[0].Confidence!.Value, 0d, 1d);
        Assert.InRange(annotated[0].Freshness!.Value, 0d, 1d);
    }

    [Fact]
    public void ToolRegistryReflectsRuntimeTooling()
    {
        var registry = new InMemoryToolRegistry(
        [
            new ToolDefinition("guide", "Explains the platform."),
            new ToolDefinition("powershell", "Runs a local command."),
        ]);

        Assert.Equal("guide", registry.Get("guide").Name);
        Assert.Equal(2, registry.All.Count);
        Assert.Contains(registry.All, t => t.Name == "powershell");
    }
}
