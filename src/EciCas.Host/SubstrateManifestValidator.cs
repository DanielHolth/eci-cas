using EciCas.Bus;
using EciCas.Core;

namespace EciCas.Host;

/// <summary>
/// Validated at startup, same shape as RoutingManifest.Validate: catches an
/// operator's typo in Substrates:Agents (an agent nobody registered, or a
/// registered agent nobody backs) before the bus starts serving.
/// </summary>
public static class SubstrateManifestValidator
{
    public static void Validate(SubstrateOptions substrates, IEnumerable<IAgent> registeredAgents)
    {
        var cognitiveAgentNames = registeredAgents.OfType<ICognitiveAgent>().Cast<IAgent>().Select(a => a.Name).ToHashSet();
        var errors = new List<string>();

        foreach (var name in substrates.Agents.Keys.Where(n => !cognitiveAgentNames.Contains(n)))
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
