using System.Text.Json;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Host.Endpoints;
using EciCas.Host.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

const string CorsPolicy = "morrow-eci";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Tier layer (env var Tier or --Tier=X) — an operator picks a bundle of
// substrate/vendor choices without editing appsettings.json directly. Unset
// means Mock, so the host is always running a tier it can name: an unnamed
// half-configuration is not something anyone asked for, and the live switch
// would have had to offer it as a destination.
//
// Not optional, so --Tier=Minmal stops at boot instead of silently running
// the base file — the same refusal the live switch gives a bad name.
var tier = builder.Configuration["Tier"] is { Length: > 0 } named ? named : "Mock";
builder.Configuration.AddJsonFile($"appsettings.{tier}.json", optional: false, reloadOnChange: false);

// Shorthand for Console:Verbose, same spirit as the bare Tier switch above.
var verbose = builder.Configuration["Verbose"];
if (!string.IsNullOrEmpty(verbose))
{
    builder.Configuration["Console:Verbose"] = verbose;
}

builder.WebHost.UseUrls(builder.Configuration["Surface:Url"] ?? "http://localhost:5179");

// One line per agent per Information-level log call, colored per agent —
// warnings/errors keep the stock two-line shape. See AgentConsoleFormatter.
builder.Logging.AddConsole(options => options.FormatterName = AgentConsoleFormatter.FormatterName)
    .AddConsoleFormatter<AgentConsoleFormatter, ConsoleFormatterOptions>();

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Surface:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"])
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddConfiguredOptions(builder.Configuration);

builder.Services.AddSingleton<BusActivityTracker>();
builder.Services.AddSingleton<IMessageBus, ChannelBus>();

builder.Services.AddSubstrates(builder.Configuration);

var stores = await builder.AddStoresAsync(tier);

builder.Services.AddAgents();

var app = builder.Build();

await BootChecks.RunAsync(app, stores);

// One budget, read once, spent both at boot and on every live tier swap.
var warmupBudgetMs = int.TryParse(builder.Configuration["Substrates:WarmupMs"], out var w) ? w : 60_000;

app.UseCors(CorsPolicy);

var jsonOptions = app.Services.GetRequiredService<JsonSerializerOptions>();
var excludedMetaKeys = (builder.Configuration.GetSection("Sse:ExcludedMetaKeys").Get<string[]>() ?? []).ToHashSet(StringComparer.Ordinal);

app.MapPersona(jsonOptions);
app.MapKnobs(jsonOptions, warmupBudgetMs);
app.MapStreams(jsonOptions, excludedMetaKeys);

await app.StartAsync();

// Before the REPL prompt, not after: the point is that the first thing a
// person types does not pay for the model load. Configurable because a
// mock-only or vendor-only tier wants far less of a budget than a local 4B
// reading weights off disk; 0 turns it off.
if (warmupBudgetMs > 0)
{
    await SubstrateWarmup.RunAsync(
        app.Services.GetRequiredService<ISubstrateProvider>(),
        app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value,
        TimeSpan.FromMilliseconds(warmupBudgetMs),
        Console.WriteLine,
        CancellationToken.None);
}

await ConsoleRepl.RunAsync(app);
