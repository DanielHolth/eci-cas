using System.Text.Json;
using EciCas.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// Correcting what Morrow thinks it learned.
///
/// The extractor is a small model doing a hard job and it will sometimes
/// mint a fact nobody said. Everything downstream treats the index as
/// disposable — it is recomputable from the utterances — so the surface is
/// allowed to edit it directly, and only it: the utterance behind a row is
/// ground truth and stays untouched, which is why this is two verbs on
/// facts and not a general archive editor.
/// </summary>
internal static class FactEndpoints
{
    public static void MapFacts(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        _ = jsonOptions;

        app.MapPut("/api/facts/{id}", async (string id, FactRevision request, IFactLog facts,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest();
            }

            return await facts.ReviseAsync(id, request.Text, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        app.MapDelete("/api/facts/{id}", async (string id, IFactLog facts, CancellationToken cancellationToken) =>
            await facts.RemoveAsync([id], cancellationToken) > 0
                ? Results.NoContent()
                : Results.NotFound());
    }
}

internal sealed record FactRevision(string Text);
