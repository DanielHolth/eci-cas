using System.Text.Json;
using EciCas.Host.TurnLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// The session feed: what a client missed, then what happens next, both as
/// the same reduced <see cref="TurnRecord"/>.
///
/// One feed, not two. There used to be a second endpoint relaying raw
/// envelopes off a wildcard bus subscriber, and it had no replay — it could
/// not have one, because a fan-out to live sockets keeps no backlog. A second
/// window opened mid-session therefore showed a full debug drawer and an
/// empty conversation, which is exactly the thing the desktop shell has to
/// not do. Removing the raw feed fixed that by subtraction: everything a
/// surface draws now comes from a projection that replays, and no envelope
/// meta leaves this process at all.
/// </summary>
internal static class StreamEndpoints
{
    public static void MapStreams(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // The same projection the disk sink reads, served two ways: what a client
        // missed, and what happens next. A client holds no reduction logic of its
        // own — see TurnLogSubscriber.
        app.MapGet("/api/log", (TurnLogSubscriber log) => Results.Json(log.Recent(), jsonOptions));

        app.MapGet("/api/log/stream", async (HttpContext context, TurnLogSubscriber log, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(cancellationToken);

            // An SSE comment, flushed immediately: browsers hold `onopen` until
            // the first byte of the body arrives, and a quiet client may wait
            // minutes for its first real record — long enough to sit there
            // reading "Disconnected" while perfectly connected.
            await context.Response.WriteAsync(": connected\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            var reader = log.Connect(out var clientId);
            try
            {
                await foreach (var record in reader.ReadAllAsync(cancellationToken))
                {
                    var json = JsonSerializer.Serialize(record, jsonOptions);
                    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — expected, not an error.
            }
            finally
            {
                log.Disconnect(clientId);
            }
        });
    }
}
