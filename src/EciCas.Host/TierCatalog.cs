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
/// configuration. <see cref="SubstrateRegistry"/> resolves agent -> provider
/// on every call, <see cref="OpenAiCompatibleSubstrateProvider"/> re-reads
/// that agent's entry for model and thinking flags on every call, and the
/// agents hold the options *object* and read its properties per turn. So a
/// switch is a handful of writes to objects everything already consults,
/// and the one that carries a whole table -- Agents -- is replaced by
/// reference rather than edited in place, so a call that is
/// already mid-fan-out reads one coherent table or the other and never a
/// half-swapped one.
///
/// Every entry here is a tier file. appsettings.json is the floor those
/// overlay and never a destination of its own -- an unset --Tier layers Mock
/// rather than leaving the floor showing, so there is no half-configured
/// state for the dropdown to have to name.
/// </summary>
public sealed class TierCatalog
{
    private readonly Dictionary<string, TierPreset> _presets;
    private readonly IReadOnlyList<TierPreset> _ordered;
    private readonly SubstrateOptions _substrates;
    private readonly RecallOptions _recall;
    private readonly LibrarianOptions _librarian;
    private readonly RuntimeKnobs _knobs;
    private readonly KnobDefaults _knobDefaults;
    private readonly object _switchLock = new();

    public TierCatalog(IEnumerable<TierPreset> presets, SubstrateOptions substrates,
        RecallOptions recall, LibrarianOptions librarian,
        RuntimeKnobs knobs, KnobDefaults knobDefaults, string active)
    {
        _ordered = presets.ToList();
        _presets = _ordered.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _substrates = substrates;
        _recall = recall;
        _librarian = librarian;
        _knobs = knobs;
        _knobDefaults = knobDefaults;

        // Normalised to the preset's own casing, not whatever --Tier typed:
        // the dropdown's <option value> is preset.Name, and a raw "minimal"
        // against an option value of "Minimal" is a match to no one but a
        // human reading both.
        Active = _presets.TryGetValue(active, out var initial) ? initial.Name : active;
    }

    /// <summary>Name of the tier in force. Starts as whatever <c>--Tier</c> said, or Mock.</summary>
    public string Active { get; private set; }

    /// <summary>Cheapest first, in the order the loader ranked them -- a list, because Dictionary.Values promises no order.</summary>
    public IReadOnlyList<TierPreset> Presets => _ordered;

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
            _substrates.Agents = preset.Agents;
            _recall.RowsPerWorker = preset.Recall.RowsPerWorker;
            _recall.MaxConcurrentRecalls = preset.Recall.MaxConcurrentRecalls;
            _recall.MaxPickedPerWorker = preset.Recall.MaxPickedPerWorker;
            _recall.PickAfterVector = preset.Recall.PickAfterVector;
            _recall.RecentRows = preset.Recall.RecentRows;
            _librarian.VectorMinScore = preset.Librarian.VectorMinScore;

            // RecallDepth is a live knob seeded from the tier, so a tier
            // switch re-seeds it. It overrides MaxPickedPerWorker, and
            // leaving a hand-dragged 5 in place while switching to a tier
            // that says 2 would silently keep the old tier's fan-out under
            // the new tier's name -- the drag is cheap to redo, the
            // confusion is not.
            _knobs.RecallDepth = preset.Recall.MaxPickedPerWorker;

            // Same re-seeding, same reason: MaxSentences, ReflectionEvery and
            // Mood are live knobs too, and leaving a hand-dragged value in
            // place across a switch would run the new tier under the old
            // tier's session experiment.
            _knobs.MaxSentences = preset.Knobs.MaxSentences;
            _knobs.ReflectionEvery = preset.Knobs.ReflectionEvery;
            _knobs.PerceptionChars = preset.Knobs.PerceptionChars;
            _knobs.ContextTurns = preset.Knobs.ContextTurns;
            _knobs.Mood = preset.Knobs.Mood;
            _knobDefaults.MaxSentences = preset.Knobs.MaxSentences;
            _knobDefaults.ReflectionEvery = preset.Knobs.ReflectionEvery;
            _knobDefaults.PerceptionChars = preset.Knobs.PerceptionChars;
            _knobDefaults.ContextTurns = preset.Knobs.ContextTurns;
            _knobDefaults.Mood = preset.Knobs.Mood;

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
    public required Dictionary<string, SubstrateAgentEntry> Agents { get; init; }
    public required RecallOptions Recall { get; init; }
    public required LibrarianOptions Librarian { get; init; }
    public required KnobDefaults Knobs { get; init; }

    /// <summary>
    /// Where this tier sits on the one axis tiers actually have: base 0
    /// (unconfigured), Mock 1, up to Super 5. Cost and capability move
    /// together across the set, so one number orders both -- and it is a
    /// number in the tier file rather than a C# enum, because the whole point
    /// of a tier is that adding one is a config change.
    /// </summary>
    public required int Rank { get; init; }

    /// <summary>
    /// Environment variables this tier's live agents need and that are not
    /// set right now. Empty does not promise the tier works -- Minimal needs
    /// llama-server up and declares no key at all -- it only rules out the
    /// failure that is knowable without making a call.
    /// </summary>
    public required IReadOnlyList<string> MissingKeys { get; init; }
}
