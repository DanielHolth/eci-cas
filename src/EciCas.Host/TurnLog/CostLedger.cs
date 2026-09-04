using System.Text.Json;

namespace EciCas.Host.TurnLog;

/// <summary>
/// What the persona has cost: this run, and every run before it.
///
/// Two numbers rather than one because they answer different questions. The
/// session total is "what is this conversation costing me", which resets when
/// the host does and is meaningless across restarts. The lifetime total is
/// "what has this thing cost me", which is only useful if it survives them —
/// so it is the one thing here that touches disk.
///
/// It counts <see cref="SubstrateTrace"/> costs, so it counts exactly what a
/// provider reported and nothing it inferred. A tier that prices at zero adds
/// zero; a provider that reports no cost at all adds nothing rather than
/// guessing, which is the same rule <see cref="TurnRecord.Cost"/> follows.
/// </summary>
public sealed class CostLedger
{
    private readonly string? _path;
    private readonly Lock _gate = new();
    private decimal _session;
    private decimal _lifetime;

    public CostLedger(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _lifetime = Load();
    }

    /// <summary>Spent since this host started.</summary>
    public decimal Session
    {
        get { lock (_gate) { return _session; } }
    }

    /// <summary>Spent since the ledger file was created, this run included.</summary>
    public decimal Lifetime
    {
        get { lock (_gate) { return _lifetime; } }
    }

    public void Add(decimal cost)
    {
        lock (_gate)
        {
            _session += cost;
            _lifetime += cost;
        }
    }

    /// <summary>
    /// Best-effort. A ledger that cannot be written is worth less than a host
    /// that will not run, and the session total is unaffected either way.
    /// </summary>
    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        decimal lifetime;
        lock (_gate)
        {
            lifetime = _lifetime;
        }

        try
        {
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new Ledger(lifetime)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private decimal Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return 0m;
        }

        try
        {
            return JsonSerializer.Deserialize<Ledger>(File.ReadAllText(_path))?.Lifetime ?? 0m;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 0m;
        }
    }

    private sealed record Ledger(decimal Lifetime);
}
