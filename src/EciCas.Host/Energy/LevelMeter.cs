using System.Text.Json;

namespace EciCas.Host.Energy;

/// <summary>Where the persona is on the cog, and how far into the current level.</summary>
/// <param name="Level">1-based. The number of teeth the avatar wears.</param>
/// <param name="Xp">Total lifetime XP — one per fact kept.</param>
/// <param name="IntoLevel">XP earned since reaching <paramref name="Level"/>.</param>
/// <param name="LevelCost">XP needed to leave <paramref name="Level"/>.</param>
public sealed record LevelState(int Level, int Xp, int IntoLevel, int LevelCost)
{
    public double Fraction => LevelCost > 0 ? Math.Clamp((double)IntoLevel / LevelCost, 0d, 1d) : 0d;
}

/// <summary>
/// Levels, earned by remembering rather than by talking.
///
/// One XP per fact the Archivist actually kept, capped at three a turn —
/// which is what makes a long conversation worth more than a long message,
/// and makes a person who says nothing memorable stay where they are. It is
/// deliberately not tokens, minutes or turns: those all reward volume, and
/// the thing worth rewarding is the relationship having gained something.
///
/// Leaving level L costs 2*L, so reaching level n takes n*(n-1) facts — 90
/// for level 10. Slow on purpose: a tooth is a permanent change to the face
/// and there are only so many of them.
///
/// Persisted beside the energy balance and for the same reason: a level
/// that reset on restart would make the cog a decoration.
/// </summary>
public sealed class LevelMeter
{
    private readonly string? _path;
    private readonly Lock _gate = new();
    private int _xp;

    /// <summary>The most facts one turn can be worth. A turn is not a grind.</summary>
    public const int PerTurnCap = 3;

    public LevelMeter(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _xp = Load()?.Xp ?? 0;
    }

    public LevelState Read()
    {
        lock (_gate)
        {
            return StateFor(_xp);
        }
    }

    /// <summary>
    /// Credit a settled turn. The surface notices the level-up by watching
    /// the number change — nothing here announces one, because the ding and
    /// the floating +1 are display concerns and the bus is not a UI channel.
    /// </summary>
    public LevelState Award(int facts)
    {
        lock (_gate)
        {
            if (facts > 0)
            {
                _xp += Math.Min(facts, PerTurnCap);
            }

            return StateFor(_xp);
        }
    }

    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        int xp;
        lock (_gate)
        {
            xp = _xp;
        }

        try
        {
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new State(xp)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Walks the levels rather than inverting n*(n-1): the closed form needs
    /// a square root and a rounding rule, and the loop is over single digits
    /// for any XP a person will ever have.
    /// </summary>
    private static LevelState StateFor(int xp)
    {
        var level = 1;
        var spent = 0;
        while (spent + (2 * level) <= xp)
        {
            spent += 2 * level;
            level++;
        }

        return new LevelState(level, xp, xp - spent, 2 * level);
    }

    private State? Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record State(int Xp);
}
