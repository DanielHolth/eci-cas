using EciCas.Agents.Action;
using EciCas.Agents.Archivist;
using EciCas.Agents.Cataloger;
using EciCas.Agents.Governance;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Identity;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Librarian;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Agents.TurnWindow;
using EciCas.Agents.Utterances;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host.Telemetry;
using EciCas.Host.TurnLog;
using EciCas.Substrates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>
/// The roster. Order is irrelevant to routing -- an agent's subscriptions
/// decide what reaches it -- but it is the closest thing the host has to a
/// list of what the persona is made of, so it is kept in pipeline order.
///
/// The last five are not agents in the persona sense: they are wildcard
/// subscribers that watch the bus and no agent knows they exist.
/// </summary>
internal static class AgentRegistration
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        // The inversion, as one boolean. True swaps four agents for two --
        // Librarian and Recall become one sweep off Perception, Archivist and
        // Cataloger become one keep -- and leaves the pair store on disk
        // untouched, so going back is the same boolean and a restart. See
        // UtteranceOptions.Enabled.
        var inverted = configuration.GetValue<bool>("Utterances:Enabled");

        RegisterAgent<PerceptionAgent>(services);
        RegisterAgent<TurnWindowAgent>(services);
        RegisterAgent<ImpulseAgent>(services);

        if (inverted)
        {
            RegisterAgent<ConsultAgent>(services);
        }
        else
        {
            RegisterAgent<LibrarianAgent>(services);
            RegisterAgent<RecallAgent>(services);
        }

        RegisterAgent<IdentityAgent>(services);
        RegisterAgent<HindsightAgent>(services);
        RegisterAgent<GovernanceAgent>(services);
        RegisterAgent<IntentAgent>(services);
        RegisterAgent<SecurityAgent>(services);
        RegisterAgent<ActionAgent>(services);
        if (inverted)
        {
            RegisterAgent<ScribeAgent>(services);
        }
        else
        {
            RegisterAgent<ArchivistAgent>(services);
            RegisterAgent<CatalogerAgent>(services);
        }

        RegisterAgent<ReflectionAgent>(services);
        RegisterAgent<ArchiveLogger>(services);
        RegisterAgent<ConsoleSubscriber>(services);
        RegisterAgent<SseBroadcaster>(services);
        RegisterAgent<TurnLogSubscriber>(services);
        RegisterAgent<TelemetryLogAgent>(services);

        if (inverted)
        {
            InvertManifest(services);
        }

        return services;
    }

    /// <summary>
    /// The manifest is validated both ways at boot -- every declared agent
    /// registered, every registered agent declared -- so a roster that
    /// changes with a flag has to change the manifest with it. Done here
    /// rather than as a second appsettings block because the point of the
    /// flag is that it is one boolean: two topologies in the config file
    /// would be two things to keep in step, and the one that drifted would
    /// only be found by flipping it.
    /// </summary>
    private static void InvertManifest(IServiceCollection services)
    {
        services.PostConfigure<RoutingManifest>(manifest =>
        {
            manifest.Agents.Remove("Librarian");
            manifest.Agents.Remove("Archivist");
            manifest.Agents.Remove("Cataloger");
            manifest.Agents["Recall"] = new ManifestAgentEntry { Subscribes = [Topics.Perception] };
            manifest.Agents["Scribe"] = new ManifestAgentEntry { Subscribes = [Topics.Perception] };
        });

        // The substrate manifest is validated the same way and has to move
        // with the roster. The four that go are exactly the four calls the
        // inversion deleted: neither of the agents that replace them thinks,
        // so neither has a tier entry. The entries stay in appsettings for
        // the flag's other position and are dropped here, in the one place
        // that already knows which position we are in.
        services.PostConfigure<SubstrateOptions>(substrates =>
        {
            substrates.Agents.Remove("Librarian");
            substrates.Agents.Remove("Recall");
            substrates.Agents.Remove("Archivist");
            substrates.Agents.Remove("Cataloger");
        });
    }

    private static void RegisterAgent<TAgent>(IServiceCollection services) where TAgent : AgentBase, IAgent
    {
        services.AddSingleton<TAgent>();
        services.AddSingleton<IAgent>(sp => sp.GetRequiredService<TAgent>());
        services.AddHostedService(sp => sp.GetRequiredService<TAgent>());
    }
}
