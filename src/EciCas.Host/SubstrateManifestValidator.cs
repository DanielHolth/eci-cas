using EciCas.Bus;
using EciCas.Core;

namespace EciCas.Host;

/// <summary>
/// Validated at startup, same shape as RoutingManifest.Validate: catches an
/// operator's typo in Substrates:Agents (an agent nobody registered, or a
/// registered agent nobody backs) before the bus starts serving.
///
/// Not everything that spends a substrate is an agent. The consolidator is a
/// call made inside a write, with its own tier entry because it wants its own
/// model, and no bus subscription at all. Those names are passed in as
/// <paramref name="nonAgentConsumers"/>: allowed to appear, never required
/// to, so a tier that does not configure one is not a drift.
/// </summary>
public static class SubstrateManifestValidator
{
    public static void Validate(SubstrateOptions substrates, IEnumerable<IAgent> registeredAgents, IEnumerable<string>? nonAgentConsumers = null)
    {
        var cognitiveAgentNames = registeredAgents.OfType<ICognitiveAgent>().Cast<IAgent>().Select(a => a.Name).ToHashSet();
        var allowed = new HashSet<string>(cognitiveAgentNames, StringComparer.Ordinal);
        allowed.UnionWith(nonAgentConsumers ?? []);
        var errors = new List<string>();

        foreach (var name in substrates.Agents.Keys.Where(n => !allowed.Contains(n)))
        {
            errors.Add($"Substrates:Agents declares '{name}' but no such cognitive agent is registered");
        }

        foreach (var name in cognitiveAgentNames.Except(substrates.Agents.Keys))
        {
            errors.Add($"cognitive agent '{name}' is registered but has no entry in Substrates:Agents");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Agent substrate manifest drift:\n" + string.Join("\n", errors));
        }
    }
}
