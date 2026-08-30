using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;

namespace EciCas.Host;

/// <summary>
/// Validated at startup, same shape as RoutingManifest.Validate: catches an
/// operator's typo in AgentSubstrates:Agents (unknown agent, missing agent,
/// or a substrate class that doesn't exist) before the bus starts serving.
/// </summary>
public static class AgentSubstrateManifestValidator
{
    public static void Validate(AgentSubstrateManifest manifest, SubstrateOptions substrates, IEnumerable<IAgent> registeredAgents)
    {
        var cognitiveAgentNames = registeredAgents.OfType<ICognitiveAgent>().Cast<IAgent>().Select(a => a.Name).ToHashSet();
        var errors = new List<string>();

        foreach (var (name, substrateClass) in manifest.Agents)
        {
            if (!cognitiveAgentNames.Contains(name))
            {
                errors.Add($"manifest declares '{name}' but no such cognitive agent is registered");
            }

            if (!substrates.Classes.ContainsKey(substrateClass))
            {
                errors.Add($"'{name}' is assigned substrate class '{substrateClass}', which is not declared under Substrates:Classes");
            }
        }

        foreach (var name in cognitiveAgentNames.Except(manifest.Agents.Keys))
        {
            errors.Add($"cognitive agent '{name}' is registered but has no entry in AgentSubstrates:Agents");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Agent substrate manifest drift:\n" + string.Join("\n", errors));
        }
    }
}
