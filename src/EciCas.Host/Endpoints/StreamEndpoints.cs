using System.Text.Json;
using EciCas.Core;
using EciCas.Host.TurnLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// The two live feeds — raw envelopes and the reduced turn record — plus
/// the replay a client that just connected asks for.
/// </summary>
internal static class StreamEndpoints
{
    public static void MapStreams(this WebApplication app, JsonSerializerOptions jsonOptions, IReadOnlySet<string> excludedMetaKeys)
    {
        // One more bus subscriber — SseBroadcaster fans every envelope out
        // to connected clients; this endpoint just relays one client's channel onto
        // the HTTP response as text/event-stream. No agent knows this exists.

        app.MapGet("/api/stream", async (HttpContext context, SseBroadcaster broadcaster, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(cancellationToken);

            // An SSE comment, flushed immediately: browsers hold `onopen` until the
            // first byte of the body arrives, and a quiet client may wait minutes
            // for its first real envelope — long enough to sit there reading
            // "Disconnected" while perfectly connected.
            await context.Response.WriteAsync(": connected\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            var reader = broadcaster.Connect(out var clientId);
            try
            {
                await foreach (var envelope in reader.ReadAllAsync(cancellationToken))
                {
                    var json = JsonSerializer.Serialize(EnvelopeDto.From(envelope, excludedMetaKeys), jsonOptions);
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
                broadcaster.Disconnect(clientId);
            }
        });

        // The same projection the disk sink reads, served two ways: what a client
        // missed, and what happens next. A client holds no reduction logic of its
        // own — see TurnLogSubscriber.
        app.MapGet("/api/log", (TurnLogSubscriber log) => Results.Json(log.Recent(), jsonOptions));

        app.MapGet("/api/log/stream", async (HttpContext context, TurnLogSubscriber log, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(cancellationToken);
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
