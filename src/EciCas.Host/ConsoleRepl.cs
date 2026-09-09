using EciCas.Agents.Perception;
using EciCas.Bus;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EciCas.Host;

/// <summary>
/// The developer's way in: type at the same host the companion app talks to,
/// and wait for the bus to go quiet rather than for a reply, since a turn is
/// not over when the persona has spoken.
/// </summary>
internal static class ConsoleRepl
{
    public static async Task RunAsync(WebApplication app)
    {
        var perception = app.Services.GetRequiredService<PerceptionAgent>();
        var activity = app.Services.GetRequiredService<BusActivityTracker>();

        // With no console to read from — a service, a container, or anything that
        // redirects stdin — the REPL's first ReadLine returns null and would take
        // the whole surface down with it. The companion UI is the real client
        // there, so run until shutdown instead.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"ECI-CAS surface listening. SSE at {string.Join(", ", app.Urls)}/api/stream. No console input — running until shutdown.");
            await app.WaitForShutdownAsync();
            return;
        }

        Console.WriteLine($"ECI-CAS surface listening. SSE at {string.Join(", ", app.Urls)}/api/stream. Type a prompt here too (empty line to exit).");
        string? line;
        while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
        {
            perception.Perceive(line);
            try
            {
                await activity.WhenIdleAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("(still working...)");
            }
        }

        await app.StopAsync();
    }
}
