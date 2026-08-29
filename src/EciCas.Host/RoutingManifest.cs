using EciCas.Core;

namespace EciCas.Host;

/// <summary>
/// One file to read the whole topology. Validated at startup against what
/// agents actually declare so it cannot drift — see plan §3.3 and test
/// RoutingManifest_WhenAgentDeclarationsDrift_FailsAtStartup.
/// </summary>
public sealed class RoutingManifest
{
    public Dictionary<string, ManifestAgentEntry> Agents { get; set; } = [];

    public static void Validate(RoutingManifest manifest, IEnumerable<IAgent> registeredAgents)
    {
        var agents = registeredAgents.ToDictionary(a => a.Name);
        var errors = new List<string>();

        foreach (var (name, entry) in manifest.Agents)
        {
            if (!agents.TryGetValue(name, out var agent))
            {
                errors.Add($"manifest declares '{name}' but no such agent is registered");
                continue;
            }

            var declared = entry.Subscribes.ToHashSet();
            var actual = agent.Subscriptions.ToHashSet();
            if (!declared.SetEquals(actual))
            {
                errors.Add($"'{name}' subscriptions drifted — manifest: [{string.Join(", ", declared)}], actual: [{string.Join(", ", actual)}]");
            }
        }

        foreach (var name in agents.Keys.Except(manifest.Agents.Keys))
        {
            errors.Add($"agent '{name}' is registered but not declared in the manifest");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Routing manifest drift:\n" + string.Join("\n", errors));
        }
    }
}

public sealed class ManifestAgentEntry
{
    public string[] Subscribes { get; set; } = [];
}
