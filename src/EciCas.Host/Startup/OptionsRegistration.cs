using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Sight;
using EciCas.Agents.Passages;
using EciCas.Agents.Reflection;
using EciCas.Agents.Toolkit;
using EciCas.Agents.Utterances;
using EciCas.Core;
using EciCas.Host.Telemetry;
using EciCas.Host.TurnLog;
using EciCas.Substrates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>
/// Every configuration section bound to the type that reads it. One list, in
/// one place, so a new section is a line here rather than a line buried in
/// three hundred others.
/// </summary>
internal static class OptionsRegistration
{
    public static IServiceCollection AddConfiguredOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GovernanceOptions>(configuration.GetSection("Governance"));
        services.Configure<RoutingManifest>(configuration.GetSection("RoutingManifest"));
        services.Configure<SubstrateOptions>(configuration.GetSection("Substrates"));
        services.Configure<ImpulseOptions>(configuration.GetSection("Impulse"));
        services.Configure<SightOptions>(configuration.GetSection("Sight"));
        services.Configure<ReflectionOptions>(configuration.GetSection("Reflection"));
        services.Configure<ToolkitOptions>(configuration.GetSection("Toolkit"));
        services.Configure<PowerShellOptions>(configuration.GetSection("PowerShell"));
        services.Configure<DiscordOptions>(configuration.GetSection("Discord"));
        services.Configure<PassageOptions>(configuration.GetSection("Passages"));
        services.Configure<UtteranceOptions>(configuration.GetSection("Utterances"));
        services.Configure<EmbeddingOptions>(configuration.GetSection("Embedding"));
        services.Configure<ConsoleOptions>(configuration.GetSection("Console"));
        services.Configure<TurnLogOptions>(configuration.GetSection("TurnLog"));
        services.Configure<TelemetryLogOptions>(configuration.GetSection("TelemetryLog"));
        services.Configure<KnobDefaults>(configuration.GetSection("Knobs"));
        services.Configure<MaintenanceOptions>(configuration.GetSection("Maintenance"));

        // The rebuild is not a tier's business, but it is resolved through
        // the same agent table every tier owns -- see MaintenanceOptions for
        // why it is installed rather than declared.
        services.PostConfigure<SubstrateOptions>(substrates =>
            configuration.GetSection("Maintenance").Get<MaintenanceOptions>()?.Install(substrates));

        // Language has no per-tier opinion, so it is not in any "Knobs"
        // section -- it lives at Shell:Dictation:Language, the base file
        // only. Read directly rather than a second Configure<> binding,
        // since the target field is on KnobDefaults, not a type of its own.
        services.PostConfigure<KnobDefaults>(knobDefaults =>
        {
            if (configuration["Shell:Dictation:Language"] is { } language)
            {
                knobDefaults.Language = language;
            }
        });

        return services;
    }
}
