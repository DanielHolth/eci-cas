using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Agents.Action;
using EciCas.Agents.Consolidator;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reasoning;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Agents.Self;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Substrates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

const string CorsPolicy = "morrow-eci";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Optional tier layer (env var Tier or --Tier=X) — an operator picks a bundle
// of substrate/vendor choices without editing appsettings.json directly. No
// default tier: unset Tier means no extra layer, i.e. today's behavior.
var tier = builder.Configuration["Tier"];
if (!string.IsNullOrEmpty(tier))
{
    builder.Configuration.AddJsonFile($"appsettings.{tier}.json", optional: true, reloadOnChange: false);
}

// Shorthand for Console:Verbose, same spirit as the bare Tier switch above.
var verbose = builder.Configuration["Verbose"];
if (!string.IsNullOrEmpty(verbose))
{
    builder.Configuration["Console:Verbose"] = verbose;
}

builder.WebHost.UseUrls(builder.Configuration["Surface:Url"] ?? "http://localhost:5179");

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Surface:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"])
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.Configure<GovernanceOptions>(builder.Configuration.GetSection("Governance"));
builder.Services.Configure<RoutingManifest>(builder.Configuration.GetSection("RoutingManifest"));
builder.Services.Configure<SubstrateOptions>(builder.Configuration.GetSection("Substrates"));
builder.Services.Configure<AgentSubstrateManifest>(builder.Configuration.GetSection("AgentSubstrates"));
builder.Services.Configure<RecallOptions>(builder.Configuration.GetSection("Recall"));
builder.Services.Configure<ReasoningOptions>(builder.Configuration.GetSection("Reasoning"));
builder.Services.Configure<ConsolidatorOptions>(builder.Configuration.GetSection("Consolidator"));
builder.Services.Configure<ReflectionOptions>(builder.Configuration.GetSection("Reflection"));
builder.Services.Configure<ConsoleOptions>(builder.Configuration.GetSection("Console"));

builder.Services.AddSingleton<BusActivityTracker>();
builder.Services.AddSingleton<IMessageBus, ChannelBus>();

builder.Services.AddSingleton<MockSubstrateProvider>();

// One named HttpClient + keyed ISubstrateProvider per configured live
// provider (see Substrates:Providers in appsettings.json) — this is how
// e.g. OpenAI and Mistral can both be live at once, each backing whichever
// substrate classes name it as their Provider.
foreach (var providerSection in builder.Configuration.GetSection("Substrates:Providers").GetChildren())
{
    var providerName = providerSection.Key;
    var baseUrl = providerSection["BaseUrl"]
        ?? throw new InvalidOperationException($"Substrate provider '{providerName}' is missing BaseUrl.");
    var apiKeyEnvironmentVariable = providerSection["ApiKeyEnvironmentVariable"];

    builder.Services.AddHttpClient(providerName, http =>
    {
        http.BaseAddress = new Uri(baseUrl);

        var apiKey = apiKeyEnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable);
        if (!string.IsNullOrEmpty(apiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    });

    builder.Services.AddKeyedSingleton<ISubstrateProvider>(providerName, (sp, key) =>
        new OpenAiCompatibleSubstrateProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient((string)key!),
            sp.GetRequiredService<IOptions<SubstrateOptions>>()));
}

builder.Services.AddSingleton<ISubstrateProvider, SubstrateRegistry>();

var securityRulesPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Security:RulesPath"] ?? "config/security-rules.json");
builder.Services.AddSingleton(SecurityRuleSet.Load(securityRulesPath));

var agentStatePath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Archive:Path"] ?? "memory.jsonl");
builder.Services.AddSingleton<IAgentStateStore>(new JsonlAgentStateStore(agentStatePath));

var archiveDirectory = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Archive:Directory"] ?? "archive");
var seedNeeded = !File.Exists(Path.Combine(archiveDirectory, "system.parquet"));
var archiveStore = new ParquetArchiveStore(archiveDirectory);
if (seedNeeded)
{
    var seedRecord = new ArchiveRecord(
        Category: "system", Topic: "identity", Subtopic: "persona", Subject: "this", Key: "name", Value: "morrow",
        Timestamp: DateTimeOffset.UtcNow, Domain: ArchiveDomain.External, Importance: 0.5);
    await archiveStore.WriteAsync([seedRecord], CancellationToken.None);
}

builder.Services.AddSingleton<IArchiveStore>(archiveStore);

RegisterAgent<PerceptionAgent>(builder.Services);
RegisterAgent<ImpulseAgent>(builder.Services);
RegisterAgent<ReasoningAgent>(builder.Services);
RegisterAgent<RecallAgent>(builder.Services);
RegisterAgent<SelfAgent>(builder.Services);
RegisterAgent<GovernanceAgent>(builder.Services);
RegisterAgent<IntentAgent>(builder.Services);
RegisterAgent<SecurityAgent>(builder.Services);
RegisterAgent<ActionAgent>(builder.Services);
RegisterAgent<ConsolidatorAgent>(builder.Services);
RegisterAgent<ReflectionAgent>(builder.Services);
RegisterAgent<ArchiveLogger>(builder.Services);
RegisterAgent<ConsoleSubscriber>(builder.Services);
RegisterAgent<SseBroadcaster>(builder.Services);

var app = builder.Build();

var manifest = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingManifest>>().Value;
RoutingManifest.Validate(manifest, app.Services.GetServices<IAgent>());

// Cheap re-read of the same cached singletons resolved above, not a re-construction.
var agentSubstrates = app.Services.GetRequiredService<IOptions<AgentSubstrateManifest>>().Value;
var substrateOptions = app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value;
AgentSubstrateManifestValidator.Validate(agentSubstrates, substrateOptions, app.Services.GetServices<IAgent>());

app.UseCors(CorsPolicy);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

app.MapPost("/api/perceive", (PerceiveRequest request, PerceptionAgent perceptionAgent) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest();
    }

    perceptionAgent.Perceive(request.Text);
    return Results.Accepted();
});

// One more bus subscriber (plan §M5) — SseBroadcaster fans every envelope out
// to connected clients; this endpoint just relays one client's channel onto
// the HTTP response as text/event-stream. No agent knows this exists.
app.MapGet("/api/stream", async (HttpContext context, SseBroadcaster broadcaster, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/event-stream";
    await context.Response.StartAsync(cancellationToken);

    var reader = broadcaster.Connect(out var clientId);
    try
    {
        await foreach (var envelope in reader.ReadAllAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(EnvelopeDto.From(envelope), jsonOptions);
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

await app.StartAsync();

var perception = app.Services.GetRequiredService<PerceptionAgent>();
var activity = app.Services.GetRequiredService<BusActivityTracker>();

Console.WriteLine($"ECI-CAS surface listening. SSE at {string.Join(", ", app.Urls)}/api/stream. Type a prompt here too (empty line to exit).");
string? line;
while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
{
    perception.Perceive(line);
    try
    {
        await activity.WhenIdleAsync(TimeSpan.FromSeconds(10));
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("(still working...)");
    }
}

await app.StopAsync();

static void RegisterAgent<TAgent>(IServiceCollection services) where TAgent : AgentBase, IAgent
{
    services.AddSingleton<TAgent>();
    services.AddSingleton<IAgent>(sp => sp.GetRequiredService<TAgent>());
    services.AddHostedService(sp => sp.GetRequiredService<TAgent>());
}

internal sealed record PerceiveRequest(string Text);
