using System.Text.Json;

namespace EciCas.Host;

/// <summary>
/// One person using this device: a display name and a chosen avatar. Not a
/// user account — there is no auth here, and iteration 1 deliberately has
/// none (see docs/roadmap.md, "Multi-user profiles, iteration 1").
/// </summary>
/// <param name="Id">Slug derived from the display name; also the directory name under archive/profiles/.</param>
public sealed record Profile(string Id, string DisplayName, string Avatar, DateTimeOffset CreatedAt);

/// <summary>
/// Reads and writes profile.json under archive/profiles/{id}/, one directory
/// per person. The directory layout is the registry — the same "the name IS
/// the index" discipline ParquetArchiveStore uses for pair files, so there is
/// no separate manifest to drift from what's on disk.
///
/// A surface concern, so it lives in the Host next to the endpoints that
/// serve it rather than on the bus: no agent knows profiles exist beyond the
/// opaque profile id that rides along on Perception's meta.
/// </summary>
public sealed class ProfileStore
{
    private const string ProfilesDirectoryName = "profiles";
    private const string ProfileFileName = "profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly object _writeLock = new();

    public ProfileStore(string archiveDirectory) =>
        _root = Path.Combine(archiveDirectory, ProfilesDirectoryName);

    /// <summary>Directory holding one profile's personal Parquet pairs and its profile.json.</summary>
    public string DirectoryFor(string profileId) => Path.Combine(_root, profileId);

    public IReadOnlyList<Profile> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(_root)
            .Select(TryRead)
            .OfType<Profile>()
            .OrderBy(profile => profile.CreatedAt)];
    }

    public Profile? Find(string profileId) =>
        IsValidId(profileId) ? TryRead(DirectoryFor(profileId)) : null;

    /// <summary>
    /// Creates a profile, or returns the existing one if the slug is already
    /// taken — two people in one household can't share a display name, and
    /// failing the second "Daniel" is friendlier than silently making
    /// "daniel-2" that nobody can tell apart in the picker.
    /// </summary>
    public (Profile Profile, bool Created) Create(string displayName, string avatar)
    {
        var id = Slug(displayName);
        if (id.Length == 0)
        {
            throw new ArgumentException("Display name has no characters usable in an id.", nameof(displayName));
        }

        lock (_writeLock)
        {
            if (TryRead(DirectoryFor(id)) is { } existing)
            {
                return (existing, false);
            }

            var profile = new Profile(id, displayName.Trim(), avatar, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(DirectoryFor(id));
            File.WriteAllText(Path.Combine(DirectoryFor(id), ProfileFileName), JsonSerializer.Serialize(profile, JsonOptions));
            return (profile, true);
        }
    }

    /// <summary>Ids come back from clients and are used as path segments, so anything outside the slug alphabet is rejected rather than sanitized.</summary>
    public static bool IsValidId(string? profileId) =>
        !string.IsNullOrEmpty(profileId) && profileId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Lowercase, ASCII, hyphen-joined — a name a directory and a URL can both carry unescaped.</summary>
    public static string Slug(string displayName)
    {
        var slug = new string([.. displayName.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]);

        return string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Profile? TryRead(string directory)
    {
        var path = Path.Combine(directory, ProfileFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A hand-edited or half-written profile.json shouldn't take the
            // whole picker down — that profile just doesn't list.
            return null;
        }
    }
}
