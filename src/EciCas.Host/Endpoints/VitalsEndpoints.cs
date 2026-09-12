using System.Text.Json;
using EciCas.Host.Energy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EciCas.Host.Endpoints;

/// <summary>
/// What Morrow is running on: energy left, and how far up the cog it is.
///
/// One endpoint for two meters because they are one thing on screen — a bar
/// under the face and the teeth around it — and a surface that had to make
/// two requests to draw one avatar would show them disagreeing for a frame.
/// </summary>
internal static class VitalsEndpoints
{
    public static void MapVitals(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        app.MapGet("/api/vitals", (EnergyMeter energy, LevelMeter levels) =>
            Results.Json(Payload(energy, levels), jsonOptions));

        // The affordance that has to exist while none of this is purchasable
        // yet: waiting 48 hours to see a full meter is not a test.
        app.MapPost("/api/vitals/fill", async (EnergyMeter energy, LevelMeter levels, CancellationToken cancellationToken) =>
        {
            energy.Fill();
            await energy.PersistAsync(cancellationToken);
            return Results.Json(Payload(energy, levels), jsonOptions);
        });

        static object Payload(EnergyMeter energy, LevelMeter levels)
        {
            var e = energy.Read();
            var l = levels.Read();
            return new
            {
                energy = new
                {
                    balanceUsd = e.BalanceUsd,
                    maxUsd = e.MaxUsd,
                    fraction = e.Fraction,
                    fullAt = e.FullAt,
                    isEmpty = e.IsEmpty,
                },
                level = new
                {
                    level = l.Level,
                    xp = l.Xp,
                    intoLevel = l.IntoLevel,
                    levelCost = l.LevelCost,
                    fraction = l.Fraction,
                },
            };
        }
    }
}
