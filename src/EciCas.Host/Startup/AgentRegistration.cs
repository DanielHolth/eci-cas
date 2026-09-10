using EciCas.Agents.Action;
using EciCas.Agents.Governance;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Identity;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Agents.TurnWindow;
using EciCas.Agents.Utterances;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host.Telemetry;
using EciCas.Host.TurnLog;
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
    public static IServiceCollection AddAgents(this IServiceCollection services)
    {
        RegisterAgent<PerceptionAgent>(services);
        RegisterAgent<TurnWindowAgent>(services);
        RegisterAgent<ImpulseAgent>(services);

        // The read side of the inverted archive: one cosine sweep off
        // Perception, replacing what used to be Librarian's selection and
        // Recall's picking. Holds Recall's name and slot -- see
        // ConsultAgent.Name.
        RegisterAgent<ConsultAgent>(services);

        RegisterAgent<IdentityAgent>(services);
        RegisterAgent<HindsightAgent>(services);
        RegisterAgent<GovernanceAgent>(services);
        RegisterAgent<IntentAgent>(services);
        RegisterAgent<SecurityAgent>(services);
        RegisterAgent<ActionAgent>(services);

        // The write side: append-only, no substrate call, replacing what
        // used to be Archivist's extraction and Cataloger's filing.
        RegisterAgent<ScribeAgent>(services);

        RegisterAgent<ReflectionAgent>(services);
        RegisterAgent<ArchiveLogger>(services);
        RegisterAgent<ConsoleSubscriber>(services);
        RegisterAgent<SseBroadcaster>(services);
        RegisterAgent<TurnLogSubscriber>(services);
        RegisterAgent<TelemetryLogAgent>(services);

        return services;
    }

    private static void RegisterAgent<TAgent>(IServiceCollection services) where TAgent : AgentBase, IAgent
    {
        services.AddSingleton<TAgent>();
        services.AddSingleton<IAgent>(sp => sp.GetRequiredService<TAgent>());
        services.AddHostedService(sp => sp.GetRequiredService<TAgent>());
    }
}
