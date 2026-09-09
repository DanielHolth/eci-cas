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

// One line per agent per Information-level log call, colored per agent —
// warnings/errors keep the stock two-line shape. See AgentConsoleFormatter.
builder.Logging.AddConsole(options => options.FormatterName = AgentConsoleFormatter.FormatterName)
    .AddConsoleFormatter<AgentConsoleFormatter, ConsoleFormatterOptions>();

var surface = builder.AddSurface();

builder.Services.AddConfiguredOptions(builder.Configuration);

builder.Services.AddSingleton<BusActivityTracker>();
builder.Services.AddSingleton<IMessageBus, ChannelBus>();

builder.Services.AddSubstrates(builder.Configuration);

var stores = await builder.AddStoresAsync();

builder.AddKnobs(tier);
builder.Services.AddAgents();

var app = builder.Build();

await BootChecks.RunAsync(app, stores);

app.UseCors(surface.CorsPolicy);

var jsonOptions = app.Services.GetRequiredService<JsonSerializerOptions>();

app.MapPersona(jsonOptions);
app.MapKnobs(jsonOptions, surface.WarmupBudgetMs);
app.MapStreams(jsonOptions, surface.ExcludedMetaKeys);

await app.StartAsync();

// Before the REPL prompt, not after: the point is that the first thing a
// person types does not pay for the model load. Configurable because a
// mock-only or vendor-only tier wants far less of a budget than a local 4B
// reading weights off disk; 0 turns it off.
if (surface.WarmupBudgetMs > 0)
{
    await SubstrateWarmup.RunAsync(
        app.Services.GetRequiredService<ISubstrateProvider>(),
        app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value,
        TimeSpan.FromMilliseconds(surface.WarmupBudgetMs),
        Console.WriteLine,
        CancellationToken.None);
}

await ConsoleRepl.RunAsync(app);
