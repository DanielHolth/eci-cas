using System.Net.Http.Headers;
using EciCas.Agents.Toolkit;
using Microsoft.Extensions.Configuration;
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
    public static IServiceCollection AddToolkits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IToolkit, PowerShellToolkit>();
        services.AddSingleton<IToolkit, GuideToolkit>();
        services.AddSingleton<IToolkit, DiscordToolkit>();

        // JSON toolkits -- see ManifestToolkit and ManifestToolkitLoader. A
        // manifest only ever reaches "approved" by a human hand-editing the
        // file; a dropped-in file that isn't yet flagged Approved is reported
        // below and skipped, never silently activated.
        var toolkitsDirectory = configuration["Toolkits:Directory"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "Toolkits");
        var manifestScan = ManifestToolkitLoader.Scan(toolkitsDirectory);

        foreach (var fileName in manifestScan.Pending)
        {
            Console.WriteLine($"[toolkits] '{fileName}' is a valid manifest but is not Approved yet -- not loaded.");
        }

        foreach (var (fileName, reason) in manifestScan.Invalid)
        {
            Console.WriteLine($"[toolkits] '{fileName}' failed to load: {reason}.");
        }

        foreach (var manifest in manifestScan.Approved)
        {
            services.AddSingleton<IToolkit>(sp => new ManifestToolkit(manifest, sp.GetRequiredService<IHttpClientFactory>()));
        }

        // Bot token read once at startup, same ApiKeyEnvironmentVariable
        // convention as Substrates:Providers -- see SubstrateRegistration.
        // Bearer scheme "Bot" is Discord's own, not OAuth's.
        var tokenEnvironmentVariable = configuration["Discord:TokenEnvironmentVariable"];
        services.AddHttpClient("discord", http =>
        {
            http.BaseAddress = new Uri("https://discord.com/api/v10/");

            var token = tokenEnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(tokenEnvironmentVariable);
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            }
        });

        services.AddSingleton<IToolkitCatalog>(_ => new ToolkitCatalog(
        [
            .. manifestScan.Approved.Select(m => new ToolkitDescriptor(m.Name, m.Description, m.Triggers)),
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
                "Introduces Morrow -- keybindings, the settings panel, and what her toolkits can do.",
                [
                    "What can you do?",
                    "What toolkits do you have?",
                    "What are you capable of?",
                    "Show me your features.",
                    "What tools can you use?",
                    "Tell me about yourself.",
                    "How do I use you?",
                    "How do you work?",
                    "What are your keybindings?",
                    "How do I talk to you?",
                    "I'm new here, how does this work?",
                    "What can I do in the settings?",
                ]),
            // "accessibility" is not a real toolkit yet -- it was one example
            // of a future one, but AccessibilityToolkit/SpeechOutput speak
            // through the Windows default SAPI voice, unrelated to the
            // browser's chosen voice, and its exemplars ("say this aloud",
            // "read it to me") were close enough to ordinary conversation to
            // misroute an ordinary turn here, echoing the person's own words
            // back in the wrong voice. Registration and catalog entry both
            // removed until it's actually designed.
            new ToolkitDescriptor(
                "discord",
                "Posts a message to Morrow's Discord channel through the bot Morrow is installed as.",
                [
                    "Post that to Discord.",
                    "Send this message to my Discord server.",
                    "Let the Discord channel know.",
                    "Message the team on Discord.",
                    "Put that in our Discord chat.",
                ]),
        ]));

        return services;
    }
}
