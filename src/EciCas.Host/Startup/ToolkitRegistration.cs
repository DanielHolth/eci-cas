using EciCas.Agents.Toolkit;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>
/// The toolkit roster: one <see cref="IToolkit"/> registration per tool
/// name, paired with a <see cref="ToolkitDescriptor"/> in the same call so
/// the two can't drift apart silently. <see cref="ToolkitHandlerAgent"/>
/// collects the <see cref="IToolkit"/> set via <c>IEnumerable&lt;IToolkit&gt;</c>
/// and dispatches by <see cref="IToolkit.Name"/>; <see cref="ToolkitManagerAgent"/>
/// and <see cref="GuideToolkit"/> both read the descriptor set via
/// <see cref="IToolkitCatalog"/> instead -- neither resolves
/// <c>IEnumerable&lt;IToolkit&gt;</c> directly, which would self-reference for
/// GuideToolkit since it is itself one of the registrations.
/// </summary>
internal static class ToolkitRegistration
{
    public static IServiceCollection AddToolkits(this IServiceCollection services)
    {
        services.AddSingleton<IToolkit, PowerShellToolkit>();
        services.AddSingleton<IToolkit, GuideToolkit>();

        services.AddSingleton<IToolkitCatalog>(_ => new ToolkitCatalog(
        [
            new ToolkitDescriptor(
                "powershell",
                "Runs PowerShell on this machine -- file and folder cleanup, disk space, process and service checks, quick system queries.",
                [
                    "Clean up my downloads folder to make some disk space.",
                    "Free up disk space on my computer.",
                    "What's taking up all the space on my hard drive?",
                    "Delete old temp files.",
                    "List the files in a folder.",
                    "Check if a program is running.",
                    "What processes are using the most memory right now?",
                    "Find and remove duplicate files.",
                    "Show me my disk usage.",
                    "Kill a frozen process.",
                    "Check my network connection.",
                    "Rename a bunch of files at once.",
                ]),
            new ToolkitDescriptor(
                "guide",
                "Lists what Morrow can currently do through its toolkits.",
                [
                    "What can you do?",
                    "What toolkits do you have?",
                    "What are you capable of?",
                    "Show me your features.",
                    "What tools can you use?",
                ]),
        ]));

        return services;
    }
}
