namespace EciCas.Core;

/// <summary>
/// Loads every agent's instruction file once, at startup, and validates it
/// against what the agent will actually fill in.
///
/// Not only the agents that call a substrate. Identity's persona, Impulse's
/// reflex reply and Governance's three notices never reach a model at all —
/// they are read by the person directly — but they colour how the persona
/// sounds just as much as a prompt does, and anything that does that is a
/// writing job rather than a programming one. They were C# constants until
/// the persona's own self-description turned out to have been unreviewable
/// for months because changing it meant a rebuild.
///
/// Both failures are startup failures on purpose. A missing file cannot
/// fall back to an empty instruction: an agent that silently loses its
/// standing text still answers, just worse, and the symptom surfaces turns
/// later as a quality complaint rather than as an error. A placeholder the
/// agent does not supply — <c>{turns}</c> mistyped as <c>{turn}</c> while
/// revising — has the same shape, so the roster below names what each file
/// is allowed to reference and anything else refuses to boot.
///
/// The mirror of that failure is a placeholder the agent supplies and the
/// file no longer names. It is quieter: <c>archivist.txt</c> without
/// <c>{text}</c> validates, boots, and hands the model a prompt with no
/// message in it, so the agent runs every turn and stores nothing. Nothing
/// is missing at startup and nothing throws later; the archive simply stays
/// empty. So the second roster names what each file cannot do without.
///
/// The two rosters are not the same list, and deriving one from the other
/// would be wrong. <c>identity.txt</c>'s name section is shipped with its
/// only line commented out on purpose — an emptied section drops the "you
/// are called ..." clause, which IdentityAgent documents as a judgement
/// about prose that belongs in the file. <c>{name}</c> is therefore allowed
/// and not required, and a rule that required every allowed placeholder
/// would refuse to boot on a deliberate configuration.
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
            ["Archivist"] = ["text"],
            ["Cataloger"] = ["cat", "topics", "fact", "text"],
            ["Reflection"] = ["turns", "revisit", "moods", "terse", "previous", "topics", "drive"],
            ["Identity"] = ["name"],
            ["Impulse"] = [],
            ["Governance"] = ["cause", "impaired", "concern"],
        };

    /// <summary>
    /// What each agent cannot do without, per section. Naming a section here
    /// also requires it to exist, which is why sections carrying no
    /// placeholder still appear: the code reads them by name, so losing one
    /// is a startup problem rather than a KeyNotFoundException on the first
    /// turn that needs it.
    ///
    /// Only the inputs are listed, not everything spliced in. A cap like
    /// {max} can be written into the prose as a number and the prompt still
    /// works; {text} or {rows} going missing leaves the model with nothing
    /// to work on and no way to say so. Identity's profile sections are
    /// absent on purpose — "Identity:Profile" picks one by name, so which
    /// exist is the person's business.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> RequiredPlaceholders =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Librarian"] = Sections((InstructionFile.MainSection, ["options", "text"])),
            ["Recall"] = Sections((InstructionFile.MainSection, ["rows", "text"])),
            ["Archivist"] = Sections((InstructionFile.MainSection, ["text"])),
            ["Cataloger"] = Sections(
                ("vocabulary", []),
                ("category", ["fact", "text"]),
                ("topic", ["cat", "topics", "fact", "text"])),
            ["Reflection"] = Sections(
                (InstructionFile.MainSection, ["turns"]),
                ("revisit", ["previous", "topics"])),
            ["Governance"] = Sections(
                ("reasoning-down", ["cause"]),
                ("less-grounded", ["impaired"]),
                ("blocked", []),
                ("blocked-with-reason", ["concern"])),
            ["Identity"] = Sections(
                ("stranger", []),
                ("name", [])),
            ["Intent"] = Sections(("fallback", [])),
        };

    private static IReadOnlyDictionary<string, string[]> Sections(params (string Section, string[] Names)[] entries) =>
        entries.ToDictionary(e => e.Section, e => e.Names, StringComparer.OrdinalIgnoreCase);

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

            RequireSections(agent, path, sections);

            _agents[agent] = sections;
        }
    }

    /// <summary>
    /// Every section the code reads by name is present, and every input it
    /// splices in is still referenced. Both messages name the file and the
    /// section, because the person who broke it was editing prose and will
    /// be looking at a text file, not a stack trace.
    /// </summary>
    private static void RequireSections(string agent, string path, IReadOnlyDictionary<string, string> sections)
    {
        if (!RequiredPlaceholders.TryGetValue(agent, out var required))
        {
            return;
        }

        foreach (var (section, names) in required)
        {
            if (!sections.TryGetValue(section, out var body))
            {
                throw new InvalidOperationException(
                    $"{path} has no '{section}' section, which {agent} reads by name. " +
                    $"It has: {string.Join(", ", sections.Keys)}.");
            }

            var present = InstructionFile.PlaceholdersIn(body);
            var missing = names.Where(n => !present.Contains(n)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{path} section '{section}' no longer names {{{string.Join("}, {", missing)}}}, " +
                    $"which {agent} fills in. Without it the agent still runs and silently does nothing.");
            }
        }
    }

    public string Directory { get; }

    public IReadOnlyCollection<string> SectionsFor(string agent) =>
        _agents.TryGetValue(agent, out var sections)
            ? (IReadOnlyCollection<string>)sections.Keys.ToList()
            : throw new KeyNotFoundException($"No instructions loaded for {agent}.");

    public string For(string agent, string section = InstructionFile.MainSection)
    {
        if (!_agents.TryGetValue(agent, out var sections))
        {
            throw new KeyNotFoundException($"No instructions loaded for {agent}.");
        }

        return sections.TryGetValue(section, out var body)
            ? body
            : throw new KeyNotFoundException(
                $"{agent}'s instruction file has no '{section}' section. It has: {string.Join(", ", sections.Keys)}.");
    }
}
