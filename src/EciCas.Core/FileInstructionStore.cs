namespace EciCas.Core;

/// <summary>
/// Loads every agent's instruction file once, at startup, and validates it
/// against what the agent will actually fill in.
///
/// Both failures are startup failures on purpose. A missing file cannot
/// fall back to an empty instruction: an agent that silently loses its
/// standing text still answers, just worse, and the symptom surfaces turns
/// later as a quality complaint rather than as an error. A placeholder the
/// agent does not supply — <c>{turns}</c> mistyped as <c>{turn}</c> while
/// revising — has the same shape, so the roster below names what each file
/// is allowed to reference and anything else refuses to boot.
/// </summary>
public sealed class FileInstructionStore : IInstructionStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _agents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each agent fills in, and therefore what its file may name. The
    /// list is the contract between the prose and the code that splices into
    /// it — the one place a hand revision can go wrong without the text
    /// looking wrong.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> KnownPlaceholders =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Intent"] = [],
            ["Librarian"] = ["options", "max", "text"],
            ["Recall"] = ["rows", "max", "text"],
            ["Archivist"] = ["known", "terse", "english", "text"],
            ["Reflection"] = ["turns", "revisit", "moods", "terse", "english", "previous", "topics"],
        };

    public FileInstructionStore(string directory)
    {
        Directory = directory;

        foreach (var (agent, allowed) in KnownPlaceholders)
        {
            var path = Path.Combine(directory, agent.ToLowerInvariant() + ".txt");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"{agent} has no instruction file. Expected it at {path}.", path);
            }

            var sections = InstructionFile.Parse(File.ReadAllText(path));
            foreach (var (section, body) in sections)
            {
                var unknown = InstructionFile.PlaceholdersIn(body).Except(allowed, StringComparer.Ordinal).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"{path} section '{section}' names {{{string.Join("}, {", unknown)}}}, which {agent} does not fill. " +
                        $"It may use: {(allowed.Length == 0 ? "no placeholders" : "{" + string.Join("}, {", allowed) + "}")}.");
                }
            }

            _agents[agent] = sections;
        }
    }

    public string Directory { get; }

    public string For(string agent, string section = InstructionFile.MainSection)
    {
        if (!_agents.TryGetValue(agent, out var sections))
        {
            throw new KeyNotFoundException($"No instructions loaded for {agent}.");
        }

        return sections.TryGetValue(section, out var body)
            ? body
            : throw new KeyNotFoundException($"{agent}'s instruction file has no '{section}' section.");
    }
}
