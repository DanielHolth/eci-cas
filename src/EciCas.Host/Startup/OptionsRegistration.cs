using EciCas.Agents.Cataloger;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Librarian;
using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
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
        services.Configure<RecallOptions>(configuration.GetSection("Recall"));
        services.Configure<LibrarianOptions>(configuration.GetSection("Librarian"));
        services.Configure<CatalogerOptions>(configuration.GetSection("Cataloger"));
        services.Configure<ImpulseOptions>(configuration.GetSection("Impulse"));
        services.Configure<ReflectionOptions>(configuration.GetSection("Reflection"));
        services.Configure<PassageOptions>(configuration.GetSection("Passages"));
        services.Configure<UtteranceOptions>(configuration.GetSection("Utterances"));
        services.Configure<EmbeddingOptions>(configuration.GetSection("Embedding"));
        services.Configure<ConsoleOptions>(configuration.GetSection("Console"));
        services.Configure<TurnLogOptions>(configuration.GetSection("TurnLog"));
        services.Configure<TelemetryLogOptions>(configuration.GetSection("TelemetryLog"));
        services.Configure<KnobDefaults>(configuration.GetSection("Knobs"));

        return services;
    }
}
