using System.Text.Json;
using EciCas.Agents.Identity;
using EciCas.Agents.Perception;
using EciCas.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// Who the persona is and the one way in: everything a client needs before a
/// turn exists.
/// </summary>
internal static class PersonaEndpoints
{
    public static void MapPersona(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // The persona's own card, read-only. Without this the personality is the one
        // configured thing with no surface at all: it colours every reply and a
        // person could only find out what it said by opening a JSONL file.
        app.MapGet("/api/persona", async (IAgentStateStore store, IInstructionStore instructions, PersonaName names,
            CancellationToken cancellationToken) =>
        {
            var stored = await store.LookupAsync([IdentityAgent.IdentityPath], maxPerPath: 1, cancellationToken);
            var text = stored.Count > 0 ? stored[0].Content : instructions.For("Identity", IdentityAgent.StrangerSection);

            var name = await names.ForAsync(cancellationToken);
            return Results.Json(new { text, name }, jsonOptions);
        });

        app.MapPost("/api/perceive", (PerceiveRequest request, PerceptionAgent perceptionAgent) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest();
            }

            perceptionAgent.Perceive(request.Text);
            return Results.Accepted();
        });

        // The persona picking a thought back up on its own, without a batch
        // having concluded to find it. The surface calls this on a rare
        // opening -- see morrow-eci/lib/greeting.ts -- and the point is that
        // the greeting is then followed by the persona actually saying
        // something of its own rather than a canned line.
        //
        // The newest passage, not the nearest. There is no query to be near:
        // nobody has said anything yet, which is the whole occasion. Newest
        // is the one choice that needs no embedder, costs no search, and
        // gives the same answer twice -- and it is already the fallback
        // Reflection drops to for exactly that reason (IPassageStore.LatestAsync).
        //
        // An empty corpus is 204, not 404: a persona that has not thought
        // anything yet is a normal early state, and the surface simply says
        // its greeting and stops.
        app.MapPost("/api/nudge", async (IPassageStore passages,
            PerceptionAgent perceptionAgent, CancellationToken cancellationToken) =>
        {
            var latest = await passages.LatestAsync(cancellationToken);
            if (latest is null || string.IsNullOrWhiteSpace(latest.Text))
            {
                return Results.NoContent();
            }

            perceptionAgent.Perceive(latest.Text, self: true);
            return Results.Accepted();
        });
    }
}

internal sealed record PerceiveRequest(string Text);
