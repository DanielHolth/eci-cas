using EciCas.Agents.Intent;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Host;

/// <summary>
/// An ordinary subscriber, not a display hook baked into any agent. No agent
/// knows this exists. By default prints only what a user actually cares
/// about per turn — what Intent said or Security blocked (both come off the
/// same events.action envelope) — leaving what Recall read and substrate
/// cost/Consolidator/Reflection writes to their own ILogger lines (see
/// appsettings.json's Logging:LogLevel and AgentConsoleFormatter). Verbose
/// restores the old exhaustive one-line-per-envelope trace for debugging.
/// </summary>
public sealed class ConsoleSubscriber : AgentBase
{
    private readonly ConsoleOptions _options;

    public ConsoleSubscriber(IMessageBus bus, BusActivityTracker activity, ILogger<ConsoleSubscriber> logger, IOptions<ConsoleOptions> options)
        : base(bus, activity, logger) => _options = options.Value;

    public override string Name => "ConsoleSubscriber";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.All];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_options.Verbose)
        {
            PrintVerbose(envelope);
            return Task.CompletedTask;
        }

        if (envelope.Topic == Topics.Action)
        {
            PrintAction(envelope);
        }

        return Task.CompletedTask;
    }

    private static void PrintAction(Envelope envelope)
    {
        var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey) ?? string.Empty;
        var verdict = envelope.Meta.Get<Verdict>(SecurityAgent.VerdictKey);
        Console.WriteLine(verdict == Verdict.Red ? $"  [blocked] {reply}" : $"> {reply}");
    }

    private static void PrintVerbose(Envelope envelope)
    {
        if (envelope.Topic == Topics.Action)
        {
            PrintAction(envelope);
        }
        else
        {
            Console.WriteLine($"  [{envelope.Topic}] {envelope.PublishedBy} ({envelope.Severity})");
        }
    }
}
