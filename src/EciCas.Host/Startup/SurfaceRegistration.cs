using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Core;
using EciCas.Host.Energy;
using EciCas.Host.TurnLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>The few surface decisions the pipeline itself has to hold: which
/// CORS policy to apply, how long boot and a live tier swap may spend warming
/// a substrate, and where the built client sits if this process is also
/// serving it.</summary>
/// <param name="CorsPolicy">Named policy registered below, applied by the caller.</param>
/// <param name="WarmupBudgetMs">One budget, read once, spent both at boot and on every live tier swap.</param>
/// <param name="ClientPath">
/// The exported client to serve at the site root, or null for none.
///
/// Null is the development case: `next dev` serves the client on its own port
/// and this host is a cross-origin API, which is what the CORS policy above
/// is for. Set, it is the shipped case — one process serves both, the client
/// and the API share an origin, and there is no CORS to get wrong on someone
/// else's machine.
/// </param>
internal sealed record HostSurface(string CorsPolicy, int WarmupBudgetMs, string? ClientPath);

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

        // Resolved the same way and for the same reason: a balance that reset
        // every restart would make the meter a decoration, since restarting is
        // cheaper than waiting.
        var energy = configuration.GetSection("Energy").Get<EnergyOptions>() ?? new EnergyOptions();
        services.AddSingleton(energy);
        services.AddSingleton(new EnergyMeter(
            energy,
            string.IsNullOrWhiteSpace(energy.Path) ? null : Path.Combine(AppContext.BaseDirectory, energy.Path)));

        // Beside it, for the same reason: a level that reset on restart would
        // make the cog a decoration.
        var levelPath = configuration["Energy:LevelPath"] ?? "level.json";
        services.AddSingleton(new LevelMeter(
            string.IsNullOrWhiteSpace(levelPath) ? null : Path.Combine(AppContext.BaseDirectory, levelPath)));

        var warmupBudgetMs = int.TryParse(configuration["Substrates:WarmupMs"], out var warmup) ? warmup : 60_000;

        // The consequence of an empty meter. Configurable because which tier
        // is "all local" is a tier-file fact, not a code one.
        var localTier = configuration["Energy:LocalTier"] ?? "Free";
        services.AddSingleton(sp => new EnergyFallback(
            sp.GetRequiredService<TierCatalog>(),
            sp.GetRequiredService<ILogger<EnergyFallback>>(),
            localTier,
            // Same warm-up the dropdown runs, for the same reason, and
            // likewise not awaited: the fallback has already happened, and the
            // debit that triggered it is on a turn's critical path.
            onSwitched: warmupBudgetMs <= 0
                ? null
                : new Action(() => _ = SubstrateWarmup.RunAsync(
                    sp.GetRequiredService<ISubstrateProvider>(),
                    // IOptions, not IOptionsMonitor: TierCatalog.Switch mutates
                    // this one bound instance in place, and a monitor would hand
                    // back its own copy, still carrying the tier just left.
                    sp.GetRequiredService<IOptions<SubstrateOptions>>().Value,
                    TimeSpan.FromMilliseconds(warmupBudgetMs),
                    Console.WriteLine,
                    CancellationToken.None))));

        // Resolved against the binary like everything else here, and absent
        // rather than empty when the directory is not there: a shell build
        // copies the export in beside the exe, a `dotnet run` during
        // development does not, and neither should have to say which it is.
        var client = configuration["Surface:ClientPath"];
        var clientPath = string.IsNullOrWhiteSpace(client)
            ? null
            : Path.Combine(AppContext.BaseDirectory, client);

        return new HostSurface(
            CorsPolicyName,
            warmupBudgetMs,
            Directory.Exists(clientPath) ? clientPath : null);
    }
}
