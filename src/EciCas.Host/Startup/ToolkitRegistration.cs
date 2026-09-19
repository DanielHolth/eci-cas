using System.Net.Http.Headers;
using EciCas.Agents.Toolkit;
using EciCas.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// Capabilities are registered in code; toolkits are manifests on disk --
/// the built-ins in <c>Toolkits/</c> next to the binary, plus approved
/// packs. <see cref="ManifestCatalog"/> binds the two and is the one roster
/// routing, dispatch, the guide and the Toolkit tab all read.
/// </summary>
internal static class ToolkitRegistration
{
    public static IServiceCollection AddToolkits(this IServiceCollection services, IConfiguration configuration, IReadOnlyList<PackManifest> packs)
    {
        services.AddSingleton<ICapability, WebSearchCapability>();
        services.AddSingleton<ICapability, GuideCapability>();
        services.AddSingleton<ICapability, PowerShellCapability>();
        services.AddSingleton<ICapability, DiscordPostCapability>();
        services.AddSingleton<ICapability, HttpCallCapability>();
        services.AddSingleton<ICapability, SpeakTextCapability>();
        services.AddSingleton<ICapability, SettingsCapability>();
        services.AddSingleton<ICapability, ToolsmithCapability>();

        services.AddSingleton<OverlayAnchor>();
        services.AddSingleton<IMorrowSettings, MorrowSettings>();

        // Providers are all registered and Search:Provider picks one at call
        // time, so switching engines is a config edit and not a rebuild.
        services.AddSingleton<ISearchProvider, DuckDuckGoSearchProvider>();
        services.AddSingleton<ISearchProvider, BraveSearchProvider>();
        services.AddHttpClient("search", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        });

        services.AddSingleton<IPageReader, PageReader>();
        services.AddHttpClient("reader", http =>
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        }).ConfigurePrimaryHttpMessageHandler(ReaderGuard.CreateHandler);

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

        var directory = configuration["Toolkits:Directory"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "Toolkits");
        ManifestSource[] sources =
        [
            new(directory),
            .. packs.Select(p => Packs.Resolve(p, p.Contributes?.Toolkits) is { } folder ? new ManifestSource(folder, p.Name) : null).OfType<ManifestSource>(),
        ];

        services.AddSingleton(sp => new ManifestCatalog(
            sources,
            configuration["Tier"] is { Length: > 0 } tier ? tier : "Mock",
            sp.GetRequiredService<IOptions<ToolkitOptions>>().Value,
            sp.GetServices<ICapability>(),
            Console.WriteLine));
        services.AddSingleton<IToolkitCatalog>(sp => sp.GetRequiredService<ManifestCatalog>());

        return services;
    }
}
