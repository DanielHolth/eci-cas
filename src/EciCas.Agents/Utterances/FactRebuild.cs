using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// The boot rebuild: decides whether a better extractor can run at all, and
/// says so out loud either way.
///
/// **Why it is allowed to be skipped, and why the skip is loud.** Every
/// reason this job cannot run is a reason the archive quietly stays worse
/// than it should be. A rebuild that was configured, never ran, and said
/// nothing is indistinguishable from a rebuild that ran and found nothing to
/// do -- and the second is the state everyone assumes they are in. So the
/// count of rows still owed a re-read is logged with the reason, and the
/// only silent path is the one where nothing is configured at all.
///
/// **Energy is not a gate, on purpose.** The meter is what the persona
/// spends on talking, and running out of it is a reason to answer more
/// cheaply, not a reason to leave the memory broken. Fixing the index is
/// exactly the work worth doing while nobody is talking. What does stop it
/// is the pair of things no amount of willingness can substitute for: a key
/// that is not set, and a network that is not there.
/// </summary>
public sealed class FactRebuild
{
    private readonly FactBackfill _backfill;
    private readonly IFactExtractor _extractor;
    private readonly IFactLog _facts;
    private readonly MaintenanceOptions _maintenance;
    private readonly SubstrateOptions _substrates;
    private readonly UtteranceOptions _utterances;
    private readonly ILogger<FactRebuild> _logger;

    public FactRebuild(FactBackfill backfill, IFactExtractor extractor, IFactLog facts,
        IOptions<MaintenanceOptions> maintenance, IOptions<SubstrateOptions> substrates,
        IOptions<UtteranceOptions> utterances, ILogger<FactRebuild> logger)
    {
        _backfill = backfill;
        _extractor = extractor;
        _facts = facts;
        _maintenance = maintenance.Value;
        _substrates = substrates.Value;
        _utterances = utterances.Value;
        _logger = logger;
    }

    public async Task<FactBackfill.Redrive> RunAsync(CancellationToken cancellationToken)
    {
        if (_maintenance.Rebuild is not { } entry)
        {
            return new FactBackfill.Redrive(0, 0, null);
        }

        var blocked = Blocked(entry);
        if (blocked is not null)
        {
            // The count is the point of the line. "Skipped" alone is a
            // shrug; "skipped, and 412 rows are still waiting" is a number
            // somebody can watch stop going up.
            var owed = (await _facts.AllAsync(cancellationToken).ConfigureAwait(false))
                .Count(f => f.WrittenBy(_maintenance.Replaces));

            _logger.LogWarning("Fact rebuild skipped: {Reason}. {Owed} row(s) are still owed a better read.", blocked, owed);
            return new FactBackfill.Redrive(0, 0, blocked);
        }

        var redrive = await _backfill.RedriveAsync(_extractor, _maintenance.Replaces, cancellationToken).ConfigureAwait(false);
        if (redrive.Stopped is not null)
        {
            _logger.LogWarning("Fact rebuild stopped after {Turns} turn(s): {Reason}.", redrive.Turns, redrive.Stopped);
        }
        else if (redrive.Turns > 0)
        {
            _logger.LogInformation("Fact rebuild: re-read {Turns} turn(s) into {Rows} row(s) with {Model}.",
                redrive.Turns, redrive.Rows, entry.Model ?? MaintenanceOptions.RebuildAgentName);
        }

        return redrive;
    }

    /// <summary>
    /// Why this cannot run, or null. Only the things willingness cannot
    /// substitute for -- see the class remarks for why the energy meter is
    /// not among them.
    /// </summary>
    private string? Blocked(SubstrateAgentEntry entry)
    {
        if (!_utterances.ExtractorEnabled)
        {
            return "the extractor is switched off, so there is no better read to do";
        }

        if (!entry.UseSubstrate || entry.Provider == "mock")
        {
            return $"Maintenance:Rebuild names '{entry.Provider}', which makes no call";
        }

        if (!_substrates.Providers.TryGetValue(entry.Provider, out var provider))
        {
            return $"Maintenance:Rebuild names provider '{entry.Provider}', which is not declared under Substrates:Providers";
        }

        var variable = provider.ApiKeyEnvironmentVariable;
        if (!string.IsNullOrEmpty(variable) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            return $"{variable} is not set";
        }

        // A weak signal deliberately: it catches the laptop with the lid
        // shut on a train and costs nothing. Anything subtler than that --
        // DNS, a captive portal, the vendor being down -- is caught by the
        // pass itself, which stops the first time a call comes back with no
        // model named.
        return NetworkInterface.GetIsNetworkAvailable() || provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            ? null
            : "there is no network";
    }
}
