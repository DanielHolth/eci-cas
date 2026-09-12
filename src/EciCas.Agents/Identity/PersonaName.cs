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
/// The name the person has given the persona.
///
/// It lives under the "persona" category rather than "assistant" because the
/// two are addressed apart on the shelf — see <see cref="AssistantScope"/>.
/// It used to be scoped per profile as well, so that two people on one
/// device each got their own Morrow; an account has one profile, and the
/// name is simply the name.
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
    private string? _cached;

    public PersonaName(IArchiveStore archive, IOptions<PersonaNameOptions> options)
    {
        _archive = archive;
        DefaultName = options.Value.DefaultName;
    }

    public string DefaultName { get; }

    public async Task<string> ForAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        var rows = await _archive.LookupAsync(Pair, cancellationToken).ConfigureAwait(false);
        var stored = rows.FirstOrDefault(r =>
            r.Subject.Equals(Subject, StringComparison.OrdinalIgnoreCase)
            && r.Key.Equals(NameKey, StringComparison.OrdinalIgnoreCase)
            && r.Value.Trim().Length > 0);

        var name = stored?.Value.Trim() ?? DefaultName;
        _cached = name;
        return name;
    }

    /// <summary>
    /// The write half of this address, and the reason it is here rather than
    /// in the vocabulary.
    ///
    /// `persona` is deliberately not a category in cataloger.txt. Adding it
    /// would make it a drawer ranked by similarity against every other, which
    /// is the exact mistake <see cref="AssistantScope"/> exists to prevent --
    /// measured 1 of 16 (refl_v4, tools/retrieval-bench/RESULTS.md). A shelf that
    /// belongs to the persona is decided BEFORE ranking, by who the fact is
    /// about, and this is that decision for the one address on it that a
    /// person can change by saying so.
    ///
    /// So it reads the fact Archivist already extracted rather than the turn
    /// text: no keyword hunt for a rename, no second model call, no chance of
    /// inventing an address. A fact about the persona whose key is a name IS
    /// the rename; there is nothing else it could be.
    ///
    /// The known failure is upstream and stays upstream. Archivist mistaking
    /// "my name is Daniel" for a fact about the assistant renames the persona
    /// to Daniel -- but that fact was already misattributed, and the line in
    /// instructions/archivist.txt that keeps the speaker on subject=user is
    /// what prevents it. Filing it under identity/name instead would not have
    /// made it right, only quiet.
    ///
    /// Returns null when the fact is about anything else, which is nearly
    /// always.
    /// </summary>
    public static ArchiveRecord? Rename(ArchiveRecord fact)
    {
        if (!AssistantScope.IsSelf(fact.Subject) || fact.Value.Trim().Length == 0)
        {
            return null;
        }

        // The last word, not a substring: "your name" and "new name" are this
        // address, "nickname" and "codename" are one word and are not.
        var words = fact.Key.Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[^1].Equals(NameKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Normalised to the address the reader looks at, not to whatever the
        // extraction happened to say: ForAsync matches Subject and Key
        // exactly, so a row filed here under key="your name" would be a row
        // nothing ever reads.
        return fact with
        {
            Category = Pair.Category,
            Topic = Pair.Topic,
            Subject = Subject,
            Key = NameKey,
            Value = fact.Value.Trim(),
        };
    }

    /// <summary>
    /// Drops every cached name. Called when anything is written, because the
    /// write that renames it looks like every other write from here — and a
    /// wrong name is a worse fault than a redundant parquet read.
    /// </summary>
    public void Forget() => _cached = null;
}
