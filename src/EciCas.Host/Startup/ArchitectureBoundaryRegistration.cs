using EciCas.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

public static class ArchitectureBoundaryRegistration
{
    public static IServiceCollection AddArchitectureBoundaries(this IServiceCollection services)
    {
        services.AddSingleton<IArchitectureBoundary, SharedCoreBoundary>();
        services.AddSingleton<IArchitectureBoundary, PlatformShellBoundary>();
        services.AddSingleton<IArchitectureBoundary, RemoteRelayBoundary>();
        services.AddSingleton<IArchitectureBoundary, SyncLayerBoundary>();
        services.AddSingleton<IArchitectureReviewService, ArchitectureReviewService>();

        services.AddSingleton<IFactReliabilityScorer, FactReliabilityScorer>();
        services.AddSingleton<IToolRegistry>(new InMemoryToolRegistry(
        [
            new ToolDefinition("powershell", "Runs a local command."),
            new ToolDefinition("guide", "Explains the platform and its capabilities."),
        ]));

        return services;
    }
}
