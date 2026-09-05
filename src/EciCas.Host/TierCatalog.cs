using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;

namespace EciCas.Host;

/// <summary>
/// Every tier the host could run, loaded at boot so one can be selected
/// live instead of restarting into it.
///
/// A tier was always a configuration overlay rather than a mode, and this
/// keeps it one: each preset is the same base-plus-overlay layering that
/// <c>--Tier=X</c> performs at startup, bound eagerly instead of applied to
/// the running process. Comparing Minimal against Default used to mean two
/// restarts and a lost session, which is enough friction that nobody
/// compares.
///
/// What makes this safe rather than clever is that nothing caches its
/// configuration. <see cref="SubstrateRegistry"/> resolves class -> provider
/// on every call, <see cref="OpenAiCompatibleSubstrateProvider"/> re-reads
/// the class entry for model and thinking flags on every call, and the
/// agents hold the options *object* and read its properties per turn. So a
/// switch is a handful of writes to objects everything already consults,
/// and the two that carry a whole table -- Classes and Agents -- are
/// replaced by reference rather than edited in place, so a call that is
/// already mid-fan-out reads one coherent table or the other and never a
/// half-swapped one.
///
/// Deliberately not applied at boot: an unset Tier leaves appsettings.json
/// exactly as it was, which is not identical to the Mock preset (base sizes
/// Recall at 50 rows, Mock at 10). Booting bare and booting --Tier=Mock
/// differ today, and this is a live switch, not a redefinition of either.
/// </summary>
public sealed class TierCatalog
{
    /// <summary>The synthetic preset standing for "no overlay" -- appsettings.json as it ships.</summary>
    public const string BaseTier = "base";

    private readonly Dictionary<string, TierPreset> _presets;
    private readonly SubstrateOptions _substrates;
    private readonly AgentSubstrateManifest _agentSubstrates;
    private readonly RecallOptions _recall;
    private readonly LibrarianOptions _librarian;
    private readonly RuntimeKnobs _knobs;
    private readonly object _switchLock = new();

    public TierCatalog(IEnumerable<TierPreset> presets, SubstrateOptions substrates,
        AgentSubstrateManifest agentSubstrates, RecallOptions recall, LibrarianOptions librarian,
        RuntimeKnobs knobs, string active)
    {
        _presets = presets.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _substrates = substrates;
        _agentSubstrates = agentSubstrates;
        _recall = recall;
        _librarian = librarian;
        _knobs = knobs;
        Active = active;
    }

    /// <summary>Name of the tier in force. Starts as whatever <c>--Tier</c> said, or <see cref="BaseTier"/>.</summary>
    public string Active { get; private set; }

    public IReadOnlyCollection<TierPreset> Presets => _presets.Values;

    /// <summary>
    /// Swaps the running configuration to <paramref name="name"/>, taking
    /// effect on the next agent call. Returns false for a name no preset
    /// carries, so a typo from the surface is a 400 rather than a silent
    /// no-op -- the same reason the boot path resolves Identity:Profile
    /// eagerly.
    ///
    /// Switching to a tier whose keys are missing is allowed on purpose:
    /// the surface greys those out, but nothing here should decide that an
    /// operator may not point at a tier and read the failure themselves.
    /// </summary>
    public bool Switch(string name)
    {
        if (!_presets.TryGetValue(name, out var preset))
        {
            return false;
        }

        // Serialised against itself only. Readers are never blocked: each
        // assignment below is a single reference or scalar write, and a turn
        // straddling one sees the old value for some agents and the new for
        // others -- which is what "takes effect on the next call" means when
        // calls are already in flight.
        lock (_switchLock)
        {
            _substrates.Classes = preset.Classes;
            _agentSubstrates.Agents = preset.Agents;
            _recall.RowsPerWorker = preset.Recall.RowsPerWorker;
            _recall.MaxConcurrentRecalls = preset.Recall.MaxConcurrentRecalls;
            _recall.MaxPickedPerWorker = preset.Recall.MaxPickedPerWorker;
            _librarian.MaxSelectedPairs = preset.Librarian.MaxSelectedPairs;

            // RecallDepth is a live knob seeded from the tier, so a tier
            // switch re-seeds it. It overrides MaxPickedPerWorker, and
            // leaving a hand-dragged 5 in place while switching to a tier
            // that says 2 would silently keep the old tier's fan-out under
            // the new tier's name -- the drag is cheap to redo, the
            // confusion is not.
            _knobs.RecallDepth = preset.Recall.MaxPickedPerWorker;

            Active = preset.Name;
        }

        return true;
    }
}

/// <summary>
/// One tier, bound. Holds whole objects rather than a diff against the
/// base: the overlay was already flattened by the configuration builder
/// that produced it, and a preset that knows only its own deltas would
/// leave the previous tier's values standing for everything it does not
/// mention.
/// </summary>
public sealed class TierPreset
{
    public required string Name { get; init; }
    public required Dictionary<string, SubstrateClassEntry> Classes { get; init; }
    public required Dictionary<string, AgentSubstrateEntry> Agents { get; init; }
    public required RecallOptions Recall { get; init; }
    public required LibrarianOptions Librarian { get; init; }

    /// <summary>
    /// Environment variables this tier's live classes need and that are not
    /// set right now. Empty does not promise the tier works -- Minimal needs
    /// llama-server up and declares no key at all -- it only rules out the
    /// failure that is knowable without making a call.
    /// </summary>
    public required IReadOnlyList<string> MissingKeys { get; init; }
}
