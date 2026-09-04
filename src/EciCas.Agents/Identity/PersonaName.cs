using System.Collections.Concurrent;
using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Identity;

public sealed class PersonaNameOptions
{
    /// <summary>
    /// What it is called before anyone has called it anything. Configuration
    /// rather than a constant because the name a person reads under the avatar
    /// on first boot is a naming decision, and changing it should not need a
    /// rebuild.
    /// </summary>
    public string DefaultName { get; set; } = "Morrow";
}

/// <summary>
/// The name this profile has given the persona.
///
/// Per profile, not shared: two people on one device each get their own
/// Morrow, and each may rename it without the other's changing. That is why
/// the record lives under the "persona" category rather than "assistant" —
/// "assistant" is in <c>Archive:SharedCategories</c>, so a name written there
/// would be one name for everybody.
///
/// Nothing seeds it. The default is a fallback, not a row, so a rename is the
/// first write that ever happens at this address and there is no stale seed to
/// lose a race with. Renaming is ordinary conversation: the person says what
/// to call it, and Archivist chooses whether that is a fact worth keeping —
/// see instructions/archivist.txt, which names the address but does not force
/// the write.
///
/// Shared between IdentityAgent (which tells Intent what it is called) and
/// GET /api/persona (which tells the surface what to print under the avatar),
/// so the two can never disagree.
/// </summary>
public sealed class PersonaName
{
    public static readonly ArchivePair Pair = new("persona", "name");
    public const string Subject = "assistant";
    public const string NameKey = "name";

    private readonly IArchiveStore _archive;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public PersonaName(IArchiveStore archive, IOptions<PersonaNameOptions> options)
    {
        _archive = archive;
        DefaultName = options.Value.DefaultName;
    }

    public string DefaultName { get; }

    public async Task<string> ForAsync(string? profileId, CancellationToken cancellationToken)
    {
        var cacheKey = profileId ?? string.Empty;
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var rows = await _archive.LookupAsync(Pair, profileId, cancellationToken).ConfigureAwait(false);
        var stored = rows.FirstOrDefault(r =>
            r.Subject.Equals(Subject, StringComparison.OrdinalIgnoreCase)
            && r.Key.Equals(NameKey, StringComparison.OrdinalIgnoreCase)
            && r.Value.Trim().Length > 0);

        var name = stored?.Value.Trim() ?? DefaultName;
        _cache[cacheKey] = name;
        return name;
    }

    /// <summary>
    /// Drops every cached name. Called when anything is written, because the
    /// write that renames it looks like every other write from here — and a
    /// wrong name is a worse fault than a redundant parquet read.
    /// </summary>
    public void Forget() => _cache.Clear();
}
