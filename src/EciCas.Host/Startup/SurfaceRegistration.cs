using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Host.TurnLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>The few surface decisions the pipeline itself has to hold: which
/// CORS policy to apply, how long boot and a live tier swap may spend warming
/// a substrate, and which meta keys never reach a client.</summary>
/// <param name="CorsPolicy">Named policy registered below, applied by the caller.</param>
/// <param name="WarmupBudgetMs">One budget, read once, spent both at boot and on every live tier swap.</param>
/// <param name="ExcludedMetaKeys">Envelope meta a stream must not carry outward.</param>
internal sealed record HostSurface(string CorsPolicy, int WarmupBudgetMs, IReadOnlySet<string> ExcludedMetaKeys);

/// <summary>
/// Where the host meets the outside: the listening address, the origins
/// allowed to call it, the one JSON shape every response and the disk sink
/// share, and the two reporting sinks a person reads afterwards.
///
/// Here rather than in Program because they are policy, and Program is a
/// running order. Configuration is parsed once, at the point of use, and
/// handed on as values rather than as section names read twice.
/// </summary>
internal static class SurfaceRegistration
{
    private const string CorsPolicyName = "morrow-eci";

    public static HostSurface AddSurface(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;

        builder.WebHost.UseUrls(configuration["Surface:Url"] ?? "http://localhost:5179");

        services.AddCors(options => options.AddPolicy(CorsPolicyName, policy =>
            policy.WithOrigins(configuration.GetSection("Surface:AllowedOrigins").Get<string[]>()
                    ?? ["http://localhost:3000"])
                .AllowAnyHeader()
                .AllowAnyMethod()));

        // One JSON shape for every surface: the HTTP endpoints and the disk
        // sink, which is the same record a client reads.
        services.AddSingleton(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });

        // Off unless asked for. TurnLog:Path is resolved against the build output
        // the same way the archive is, so the log sits beside the persona it
        // describes rather than wherever the process happened to start.
        var turnLogPath = configuration["TurnLog:Path"];
        if (!string.IsNullOrWhiteSpace(turnLogPath))
        {
            configuration["TurnLog:Path"] = Path.Combine(AppContext.BaseDirectory, turnLogPath);
            services.AddSingleton<ITurnLogSink, JsonlTurnLogSink>();
        }

        // Resolved against the binary the same way, and on by default: the session
        // total is arithmetic, but the lifetime total is only worth showing if it
        // outlives the process that spent it.
        var costPath = configuration["TurnLog:CostPath"] ?? "cost.json";
        services.AddSingleton(new CostLedger(
            string.IsNullOrWhiteSpace(costPath) ? null : Path.Combine(AppContext.BaseDirectory, costPath)));

        return new HostSurface(
            CorsPolicyName,
            int.TryParse(configuration["Substrates:WarmupMs"], out var warmup) ? warmup : 60_000,
            (configuration.GetSection("Sse:ExcludedMetaKeys").Get<string[]>() ?? []).ToHashSet(StringComparer.Ordinal));
    }
}
