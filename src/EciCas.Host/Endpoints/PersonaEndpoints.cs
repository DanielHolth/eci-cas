using System.Text.Json;
using EciCas.Agents.Identity;
using EciCas.Agents.Perception;
using EciCas.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// Who the persona is, who is talking to it, and the one way in: everything
/// a client needs before a turn exists.
/// </summary>
internal static class PersonaEndpoints
{
    public static void MapPersona(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        app.MapGet("/api/profiles", (ProfileStore profiles) => Results.Json(profiles.List(), jsonOptions));

        // The persona's own card, read-only. Without this the personality is the one
        // configured thing with no surface at all: it colours every reply and a
        // person could only find out what it said by opening a JSONL file.
        app.MapGet("/api/persona", async (IAgentStateStore store, IInstructionStore instructions, PersonaName names,
            string? profileId, CancellationToken cancellationToken) =>
        {
            var stored = await store.LookupAsync([IdentityAgent.IdentityPath], maxPerPath: 1, cancellationToken);
            var text = stored.Count > 0 ? stored[0].Content : instructions.For("Identity", IdentityAgent.StrangerSection);

            // The name is per profile, so an unnamed caller (the picker, before
            // anyone has chosen) gets the default rather than someone else's.
            var name = await names.ForAsync(ProfileStore.IsValidId(profileId) ? profileId : null, cancellationToken);
            return Results.Json(new { text, name }, jsonOptions);
        });

        app.MapPost("/api/profiles", (CreateProfileRequest request, ProfileStore profiles) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Avatar))
            {
                return Results.BadRequest();
            }

            if (ProfileStore.Slug(request.DisplayName).Length == 0)
            {
                return Results.BadRequest();
            }

            var (profile, created) = profiles.Create(request.DisplayName, request.Avatar);

            // A name already in use comes back as the existing profile rather than a
            // conflict: in a household picker, "that's already you" is the answer the
            // client wants, and it can tell the two apart by the status code.
            return created
                ? Results.Created($"/api/profiles/{profile.Id}", profile)
                : Results.Json(profile, jsonOptions);
        });

        app.MapPost("/api/perceive", (PerceiveRequest request, PerceptionAgent perceptionAgent, ProfileStore profiles) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest();
            }

            // An unknown profile id is rejected rather than ignored — silently
            // attributing one person's turn to the device-wide drive state would
            // colour the persona's mood for everybody.
            if (!string.IsNullOrEmpty(request.ProfileId) && profiles.Find(request.ProfileId) is null)
            {
                return Results.NotFound();
            }

            perceptionAgent.Perceive(request.Text, request.ProfileId);
            return Results.Accepted();
        });
    }
}

internal sealed record PerceiveRequest(string Text, string? ProfileId = null);

internal sealed record CreateProfileRequest(string DisplayName, string Avatar);
