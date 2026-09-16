using EciCas.Agents.Toolkit;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>
/// The toolkit roster: one <see cref="IToolkit"/> registration per tool
/// name. <see cref="ToolkitHandlerAgent"/> collects the whole set via
/// <c>IEnumerable&lt;IToolkit&gt;</c> and dispatches by <see cref="IToolkit.Name"/>,
/// so adding a tool here is the entire change -- the agent itself never
/// grows a new branch.
/// </summary>
internal static class ToolkitRegistration
{
    public static IServiceCollection AddToolkits(this IServiceCollection services)
    {
        services.AddSingleton<IToolkit, PowerShellToolkit>();
        return services;
    }
}
