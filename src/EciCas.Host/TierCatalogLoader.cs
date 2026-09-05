using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Substrates;
using Microsoft.Extensions.Configuration;

namespace EciCas.Host;

/// <summary>
/// Finds the tier files beside the binary and binds each one the way boot
/// would have layered it.
/// </summary>
public static class TierCatalogLoader
{
    /// <summary>
    /// A tier is any <c>appsettings.X.json</c> that declares substrate
    /// classes. Recognising them by content rather than by a hardcoded list
    /// is what stops a sixth tier from needing a code change to appear in
    /// the dropdown -- and what keeps ASP.NET's own environment files
    /// (appsettings.Development.json) out of it, since they declare no
    /// classes.
    /// </summary>
    public static IReadOnlyList<TierPreset> Load(string directory)
    {
        var basePath = Path.Combine(directory, "appsettings.json");
        var presets = new List<TierPreset> { Bind(TierCatalog.BaseTier, Layer(basePath)) };

        foreach (var path in Directory.GetFiles(directory, "appsettings.*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path)["appsettings.".Length..];
            var layered = Layer(basePath, path);
            if (layered.GetSection("Substrates:Classes").GetChildren().Any())
            {
                presets.Add(Bind(name, layered));
            }
        }

        // Ordered by what a tier costs and can do, not by filename: the
        // dropdown is a dial from cheapest to best, and Ordinal on the file
        // name put Budget above Minimal. Rank lives in the tier file so a
        // sixth tier still needs no code change to place itself.
        return presets.OrderBy(p => p.Rank).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static IConfigurationRoot Layer(string basePath, string? overlay = null)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(basePath, optional: false, reloadOnChange: false);
        if (overlay is not null)
        {
            builder.AddJsonFile(overlay, optional: false, reloadOnChange: false);
        }

        return builder.Build();
    }

    private static TierPreset Bind(string name, IConfigurationRoot configuration)
    {
        var substrates = configuration.GetSection("Substrates").Get<SubstrateOptions>() ?? new SubstrateOptions();
        var agents = configuration.GetSection("AgentSubstrates").Get<AgentSubstrateManifest>() ?? new AgentSubstrateManifest();
        var recall = configuration.GetSection("Recall").Get<RecallOptions>() ?? new RecallOptions();
        var librarian = configuration.GetSection("Librarian").Get<LibrarianOptions>() ?? new LibrarianOptions();

        // Validated here rather than on selection, so a broken tier file
        // stops the host at boot with every other tier's problems listed
        // too. Finding out at the moment someone drags the dropdown is
        // finding out during a conversation.
        var errors = new List<string>();
        foreach (var (className, entry) in substrates.Classes)
        {
            if (entry.Provider != "mock" && !substrates.Providers.ContainsKey(entry.Provider))
            {
                errors.Add($"class '{className}' names provider '{entry.Provider}', which is not declared under Substrates:Providers");
            }
        }

        foreach (var (agent, entry) in agents.Agents)
        {
            if (!substrates.Classes.ContainsKey(entry.Class))
            {
                errors.Add($"agent '{agent}' is assigned class '{entry.Class}', which this tier does not declare");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Tier '{name}' is not loadable:\n" + string.Join("\n", errors));
        }

        // Only the providers this tier actually reaches for. Default needs
        // both vendor keys; Minimal needs neither and is still the tier most
        // likely to be unreachable, which is why the surface says "keys
        // missing" rather than "unavailable".
        var missing = substrates.Classes.Values
            .Select(c => c.Provider)
            .Where(p => p != "mock")
            .Distinct(StringComparer.Ordinal)
            .Select(p => substrates.Providers[p].ApiKeyEnvironmentVariable)
            .Where(v => !string.IsNullOrEmpty(v) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        return new TierPreset
        {
            Name = name,
            Classes = substrates.Classes,
            Agents = agents.Agents,
            Recall = recall,
            Librarian = librarian,
            Rank = configuration.GetValue<int>("Tier:Rank"),
            MissingKeys = missing,
        };
    }
}
