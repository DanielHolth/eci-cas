using System.Text.Json;

namespace EciCas.Host.Energy;

/// <summary>How much is left, and what that is a fraction of.</summary>
/// <param name="BalanceUsd">Remaining spend, in USD.</param>
/// <param name="MaxUsd">The ceiling M the balance is measured against.</param>
/// <param name="Fraction">0..1. What the meter shows.</param>
/// <param name="FullAt">When the bucket next reaches M, or null if it is already there.</param>
public sealed record EnergyLevel(decimal BalanceUsd, decimal MaxUsd, double Fraction, DateTimeOffset? FullAt)
{
    /// <summary>
    /// Nothing left to spend remotely. The caller's cue to switch to Local —
    /// not to refuse a turn, which is the one thing this must never cause.
    /// </summary>
    public bool IsEmpty => BalanceUsd <= 0m;
}

/// <summary>
/// The token bucket behind the energy meter.
///
/// It is a bucket rather than a monthly allowance because a bucket is both
/// the correct rate limiter and the better fiction: it refills while nobody
/// is watching, a quiet week banks a reserve, and running it dry is a state
/// Morrow can be in rather than an error a dialog has to report.
///
/// Regeneration is computed from elapsed wall-clock time rather than ticked
/// by a timer. A timer would have to run to be correct, which makes every
/// restart and every suspended laptop a small theft; elapsed time is right
/// by construction and costs nothing while the host is idle.
///
/// It spends what <see cref="TurnLog.CostLedger"/> counts — the same
/// SubstrateTrace cost signal, from the same provider-reported numbers — so
/// the two can never disagree about what a conversation cost. The ledger
/// records history and the meter rations the future, but there is one
/// source of truth underneath both.
///
/// Deliberately not authoritative. A determined person can edit the file,
/// and that is acceptable: the relay meters the spend that actually costs
/// money, server-side, per owning account. This is the client's honest copy
/// so the bar can move without a round trip.
/// </summary>
public sealed class EnergyMeter
{
    private readonly EnergyOptions _options;
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private decimal _balance;
    private DateTimeOffset _asOf;

    public EnergyMeter(EnergyOptions options, string? path, TimeProvider? time = null)
    {
        _options = options;
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _time = time ?? TimeProvider.System;

        var now = _time.GetUtcNow();
        var loaded = Load();

        // A meter with no file starts full. The alternative — starting empty
        // and making a new person wait — reads as a broken install, and the
        // first session is the one that has to land.
        _balance = loaded?.BalanceUsd ?? _options.MaxUsd;
        _asOf = loaded?.AsOf ?? now;

        // Clamp forward for a file written under a larger M, and clamp the
        // timestamp for a clock that has gone backwards.
        if (_asOf > now)
        {
            _asOf = now;
        }

        Regenerate(now);
    }

    /// <summary>What the meter reads right now, regeneration included.</summary>
    public EnergyLevel Read()
    {
        lock (_gate)
        {
            Regenerate(_time.GetUtcNow());
            return Level();
        }
    }

    /// <summary>
    /// Debit what a call cost. Returns the level after spending.
    ///
    /// The balance is allowed to go to zero but never below it. Overdraft
    /// would mean a person who spent one expensive turn owes time before
    /// anything works again, and "you are in debt" is not a thing a
    /// companion should be able to say.
    /// </summary>
    public EnergyLevel Spend(decimal costUsd)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Regenerate(now);

            if (costUsd > 0m)
            {
                _balance = Math.Max(0m, _balance - costUsd);
            }

            return Level();
        }
    }

    /// <summary>
    /// Refill to M. For a purchase that grants energy outright, and for the
    /// settings affordance that has to exist while none of this is real yet.
    /// </summary>
    public EnergyLevel Fill()
    {
        lock (_gate)
        {
            _balance = _options.MaxUsd;
            _asOf = _time.GetUtcNow();
            return Level();
        }
    }

    /// <summary>
    /// Best-effort, on the same terms as the cost ledger: a balance that
    /// cannot be written is worth less than a host that will not run. The
    /// cost of losing it is a meter that reads full, which errs towards the
    /// person.
    /// </summary>
    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        State state;
        lock (_gate)
        {
            Regenerate(_time.GetUtcNow());
            state = new State(_balance, _asOf);
        }

        try
        {
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(state), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Credit elapsed time, then advance the watermark. Caller holds the gate.</summary>
    private void Regenerate(DateTimeOffset now)
    {
        var elapsed = now - _asOf;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        if (_balance < _options.MaxUsd)
        {
            var gained = _options.RegenPerHourUsd * (decimal)elapsed.TotalHours;
            _balance = Math.Min(_options.MaxUsd, _balance + gained);
        }

        _asOf = now;
    }

    /// <summary>Caller holds the gate.</summary>
    private EnergyLevel Level()
    {
        var max = _options.MaxUsd;
        var fraction = max > 0m ? (double)(_balance / max) : 1d;

        DateTimeOffset? fullAt = null;
        if (_balance < max && _options.RegenPerHourUsd > 0m)
        {
            var hours = (double)((max - _balance) / _options.RegenPerHourUsd);
            fullAt = _asOf.AddHours(hours);
        }

        return new EnergyLevel(_balance, max, Math.Clamp(fraction, 0d, 1d), fullAt);
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

    private sealed record State(decimal BalanceUsd, DateTimeOffset AsOf);
}
