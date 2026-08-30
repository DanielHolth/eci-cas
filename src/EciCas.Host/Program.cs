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

builder.WebHost.UseUrls(builder.Configuration["Surface:Url"] ?? "http://localhost:5179");

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Surface:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"])
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.Configure<GovernanceOptions>(builder.Configuration.GetSection("Governance"));
builder.Services.Configure<RoutingManifest>(builder.Configuration.GetSection("RoutingManifest"));
builder.Services.Configure<BudgetOptions>(builder.Configuration.GetSection("Budget"));
builder.Services.Configure<SubstrateProviderOptions>(builder.Configuration.GetSection("SubstrateProvider"));
builder.Services.Configure<RecallOptions>(builder.Configuration.GetSection("Recall"));
builder.Services.Configure<ConsolidatorOptions>(builder.Configuration.GetSection("Consolidator"));
builder.Services.Configure<ReflectionOptions>(builder.Configuration.GetSection("Reflection"));

builder.Services.AddSingleton<BusActivityTracker>();
builder.Services.AddSingleton<IMessageBus, ChannelBus>();

builder.Services.AddSingleton<MockSubstrateProvider>();
builder.Services.AddHttpClient<OpenAiCompatibleSubstrateProvider>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<SubstrateProviderOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl);

    var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
    if (!string.IsNullOrEmpty(apiKey))
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
});
builder.Services.AddSingleton<ISubstrateProvider, SubstrateRegistry>();

var securityRulesPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Security:RulesPath"] ?? "config/security-rules.json");
builder.Services.AddSingleton(SecurityRuleSet.Load(securityRulesPath));

var archivePath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Archive:Path"] ?? "memory.jsonl");
builder.Services.AddSingleton<IArchiveStore>(new JsonlArchiveStore(archivePath));

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
