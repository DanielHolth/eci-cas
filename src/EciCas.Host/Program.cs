using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EciCas.Agents.Action;
using EciCas.Agents.Archivist;
using EciCas.Agents.Governance;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Identity;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using EciCas.Host.TurnLog;
using EciCas.Substrates;
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

// One line per agent per Information-level log call, colored per agent —
// warnings/errors keep the stock two-line shape. See AgentConsoleFormatter.
builder.Logging.AddConsole(options => options.FormatterName = AgentConsoleFormatter.FormatterName)
    .AddConsoleFormatter<AgentConsoleFormatter, ConsoleFormatterOptions>();

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
builder.Services.Configure<LibrarianOptions>(builder.Configuration.GetSection("Librarian"));
builder.Services.Configure<ArchivistOptions>(builder.Configuration.GetSection("Archivist"));
builder.Services.Configure<ReflectionOptions>(builder.Configuration.GetSection("Reflection"));
builder.Services.Configure<PassageOptions>(builder.Configuration.GetSection("Passages"));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.Configure<ConsoleOptions>(builder.Configuration.GetSection("Console"));
builder.Services.Configure<TurnLogOptions>(builder.Configuration.GetSection("TurnLog"));

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
    var timeoutMs = int.TryParse(providerSection["TimeoutMs"], out var t) ? t : 20_000;
    var circuitOpen = TimeSpan.FromMilliseconds(int.TryParse(providerSection["CircuitOpenMs"], out var c) ? c : 5_000);

    builder.Services.AddHttpClient(providerName, http =>
    {
        http.BaseAddress = new Uri(baseUrl);
        http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

        var apiKey = apiKeyEnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable);
        if (!string.IsNullOrEmpty(apiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    });

    builder.Services.AddKeyedSingleton<ISubstrateProvider>(providerName, (sp, key) =>
        new OpenAiCompatibleSubstrateProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient((string)key!),
            sp.GetRequiredService<IOptions<SubstrateOptions>>(),
            circuitOpen));
}

builder.Services.AddSingleton<ISubstrateProvider, SubstrateRegistry>();

// The embedder backing the passage corpus. Local ONNX by default so the
// minimal tier keeps its memory offline; "openai" borrows the named
// HttpClient a completion provider already configured, so BaseUrl and the
// key's environment variable are declared exactly once.
var embeddingProvider = builder.Configuration["Embedding:Provider"] ?? "onnx";
Func<IServiceProvider, IEmbeddingProvider> embedderFactory;
switch (embeddingProvider.ToLowerInvariant())
{
    case "onnx":
        builder.Services.AddSingleton<OnnxEmbeddingProvider>();
        embedderFactory = sp => sp.GetRequiredService<OnnxEmbeddingProvider>();
        break;
    case "openai":
        var apiProvider = builder.Configuration["Embedding:ApiProvider"] ?? "openai";
        embedderFactory = sp => new OpenAiCompatibleEmbeddingProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(apiProvider),
            sp.GetRequiredService<IOptions<EmbeddingOptions>>(),
            sp.GetRequiredService<ILogger<OpenAiCompatibleEmbeddingProvider>>());
        break;
    // "api" is docs/architecture.md's spelling of the same thing and is
    // accepted so the two cannot disagree in silence.
    case "api":
        goto case "openai";
    case "none":
        embedderFactory = _ => new NullEmbeddingProvider();
        break;
    default:
        // Previously this branch was the default, so a typo turned the whole
        // passage corpus off without a word: no warning, no error, just a
        // persona that never remembers a thought and no way to tell that
        // from "the weights aren't downloaded yet". "none" still means none;
        // anything else is a mistake and says so.
        throw new InvalidOperationException(
            $"Embedding:Provider is \"{embeddingProvider}\". Valid values are \"onnx\" (local weights), " +
            "\"openai\" (an OpenAI-compatible embeddings endpoint, also spelled \"api\"), and \"none\".");
}

// Wrapped whichever way it was built. Librarian and Hindsight embed the same
// perception text on the same turn, and the ONNX session serializes on a
// lock, so the second was waiting for the first and then recomputing an
// identical vector. Deduplicating here keeps both agents unaware of each
// other and keeps the vector off the bus.
builder.Services.AddSingleton<IEmbeddingProvider>(sp => new CachingEmbeddingProvider(
    embedderFactory(sp), sp.GetRequiredService<ILogger<CachingEmbeddingProvider>>()));


var securityRulesPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Security:RulesPath"] ?? "config/security-rules.json");
builder.Services.AddSingleton(SecurityRuleSet.Load(securityRulesPath));

// Agent state, not the archive: a JSONL side-store the Identity persona
// entry lives in. It sat under "Archive:" for long enough that an
// existing local override may still say so — that key is still read.
var agentStatePath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["AgentState:Path"] ?? builder.Configuration["Archive:Path"] ?? "memory.jsonl");
var agentStateStore = new JsonlAgentStateStore(agentStatePath);
builder.Services.AddSingleton<IAgentStateStore>(agentStateStore);

// Instructions before anything that reads one. Files rather than
// appsettings, because paragraph prose inside a JSON string means escaped
// newlines, no wrapping and a syntax error one stray quote away. Loaded
// eagerly so a missing file or a mistyped placeholder stops the host here,
// where it is one message, rather than degrading a single agent quietly at
// turn time.
var instructionStore = new FileInstructionStore(
    Path.Combine(AppContext.BaseDirectory, builder.Configuration["Instructions:Directory"] ?? "instructions"));

// Who Morrow starts as lives in instructions/identity.txt; who Morrow has
// become lives in the state store, and the store wins. Seeded only when the
// store has nothing there, so a persona that has grown past the file — by
// hand, or later by Morrow itself — is never overwritten by a redeploy.
//
// Which one is live is printed, because the asymmetry bites in one
// direction: the file is the visible artefact and the store is a line in a
// JSONL nobody opens, so an edit to the file that changed nothing looks
// exactly like an edit that worked. It went unnoticed for months once.
var storedIdentity = await agentStateStore.LookupAsync([IdentityAgent.IdentityPath], maxPerPath: 1, CancellationToken.None);
if (storedIdentity.Count == 0)
{
    await agentStateStore.WriteAsync(
        [new AgentStateRecord(IdentityAgent.IdentityPath, instructionStore.For("Identity"), DateTimeOffset.UtcNow, ArchiveDomain.Internal)],
        CancellationToken.None);
    Console.WriteLine($"Identity seeded from {Path.Combine(instructionStore.Directory, "identity.txt")}.");
}
else
{
    Console.WriteLine($"Identity read from {agentStatePath} (stored {storedIdentity[0].Timestamp:yyyy-MM-dd}); " +
        "instructions/identity.txt seeds a new persona only. Delete that entry to re-seed from the file.");
}


var archiveDirectory = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Archive:Directory"] ?? "archive");
// One record, not a migration: the archive that isn't there yet starts as
// the persona knowing its own name, and everything else is learned.
var seedNeeded = !File.Exists(ParquetArchiveStore.PairPathFor(archiveDirectory, new ArchivePair("assistant", "identity")));
var archiveStore = new ParquetArchiveStore(archiveDirectory,
    builder.Configuration.GetSection("Archive:SharedCategories").Get<string[]>());
if (seedNeeded)
{
    var seedRecord = new ArchiveRecord(
        Category: "assistant", Topic: "identity", Subtopic: "persona", Subject: "this", Key: "name", Value: "morrow",
        Timestamp: DateTimeOffset.UtcNow, Domain: ArchiveDomain.External, Importance: 0.5);
    await archiveStore.WriteAsync([seedRecord], profileId: null, CancellationToken.None);
}

builder.Services.AddSingleton<IArchiveStore>(archiveStore);

// Built above, because the persona seed reads from it. Registered here so
// every agent gets the same loaded instance.
builder.Services.AddSingleton<IInstructionStore>(instructionStore);

// The passage corpus lives beside the pair files, in the shared tier only —
// a self-critique belongs to the persona the way the "assistant" category
// already does.
builder.Services.AddSingleton<IPassageStore>(new ParquetPassageStore(archiveDirectory));


// Profiles live beside the archive they scope — one directory per person
// under archive/profiles/. A surface concern, not a bus citizen.
builder.Services.AddSingleton(new ProfileStore(archiveDirectory));

// One JSON shape for every surface: the HTTP endpoints below and the disk
// sink, which is the same record a client reads.
builder.Services.AddSingleton(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
});

// Off unless asked for. TurnLog:Path is resolved against the build output
// the same way the archive is, so the log sits beside the persona it
// describes rather than wherever the process happened to start.
var turnLogPath = builder.Configuration["TurnLog:Path"];
if (!string.IsNullOrWhiteSpace(turnLogPath))
{
    builder.Configuration["TurnLog:Path"] = Path.Combine(AppContext.BaseDirectory, turnLogPath);
    builder.Services.AddSingleton<ITurnLogSink, JsonlTurnLogSink>();
}

RegisterAgent<PerceptionAgent>(builder.Services);
RegisterAgent<ImpulseAgent>(builder.Services);
RegisterAgent<LibrarianAgent>(builder.Services);
RegisterAgent<RecallAgent>(builder.Services);
RegisterAgent<IdentityAgent>(builder.Services);
RegisterAgent<HindsightAgent>(builder.Services);
RegisterAgent<GovernanceAgent>(builder.Services);
RegisterAgent<IntentAgent>(builder.Services);
RegisterAgent<SecurityAgent>(builder.Services);
RegisterAgent<ActionAgent>(builder.Services);
RegisterAgent<ArchivistAgent>(builder.Services);
RegisterAgent<ReflectionAgent>(builder.Services);
RegisterAgent<ArchiveLogger>(builder.Services);
RegisterAgent<ConsoleSubscriber>(builder.Services);
RegisterAgent<SseBroadcaster>(builder.Services);
RegisterAgent<TurnLogSubscriber>(builder.Services);

var app = builder.Build();

var manifest = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingManifest>>().Value;
RoutingManifest.Validate(manifest, app.Services.GetServices<IAgent>());

// Cheap re-read of the same cached singletons resolved above, not a re-construction.
var agentSubstrates = app.Services.GetRequiredService<IOptions<AgentSubstrateManifest>>().Value;
var substrateOptions = app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value;
AgentSubstrateManifestValidator.Validate(agentSubstrates, substrateOptions, app.Services.GetServices<IAgent>());

// A model swap is the one event that can take a note away, and it does it
// without a log line — so it is checked here, before anything searches.
var embedder = app.Services.GetRequiredService<IEmbeddingProvider>();
PassageCorpus.EnsureModelAgreement(
    await app.Services.GetRequiredService<IPassageStore>().StampedModelsAsync(CancellationToken.None),
    embedder.ModelId);

app.UseCors(CorsPolicy);

var jsonOptions = app.Services.GetRequiredService<JsonSerializerOptions>();

app.MapGet("/api/profiles", (ProfileStore profiles) => Results.Json(profiles.List(), jsonOptions));

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

// One more bus subscriber — SseBroadcaster fans every envelope out
// to connected clients; this endpoint just relays one client's channel onto
// the HTTP response as text/event-stream. No agent knows this exists.
var excludedMetaKeys = (builder.Configuration.GetSection("Sse:ExcludedMetaKeys").Get<string[]>() ?? []).ToHashSet(StringComparer.Ordinal);

app.MapGet("/api/stream", async (HttpContext context, SseBroadcaster broadcaster, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/event-stream";
    await context.Response.StartAsync(cancellationToken);

    // An SSE comment, flushed immediately: browsers hold `onopen` until the
    // first byte of the body arrives, and a profile-scoped client may wait
    // minutes for its first real envelope — long enough to sit there
    // reading "Disconnected" while perfectly connected.
    await context.Response.WriteAsync(": connected\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);

    var profileId = context.Request.Query["profileId"].ToString();
    var reader = broadcaster.Connect(string.IsNullOrEmpty(profileId) ? null : profileId, out var clientId);
    try
    {
        await foreach (var envelope in reader.ReadAllAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(EnvelopeDto.From(envelope, excludedMetaKeys), jsonOptions);
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

// The same projection the disk sink reads, served two ways: what a client
// missed, and what happens next. A client holds no reduction logic of its
// own — see TurnLogSubscriber.
app.MapGet("/api/log", (HttpContext context, TurnLogSubscriber log) =>
{
    var profileId = context.Request.Query["profileId"].ToString();
    return Results.Json(log.Recent(string.IsNullOrEmpty(profileId) ? null : profileId), jsonOptions);
});

app.MapGet("/api/log/stream", async (HttpContext context, TurnLogSubscriber log, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/event-stream";
    await context.Response.StartAsync(cancellationToken);
    await context.Response.WriteAsync(": connected\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);

    var profileId = context.Request.Query["profileId"].ToString();
    var reader = log.Connect(string.IsNullOrEmpty(profileId) ? null : profileId, out var clientId);
    try
    {
        await foreach (var record in reader.ReadAllAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(record, jsonOptions);
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
        log.Disconnect(clientId);
    }
});

await app.StartAsync();

var perception = app.Services.GetRequiredService<PerceptionAgent>();
var activity = app.Services.GetRequiredService<BusActivityTracker>();

// With no console to read from — a service, a container, or anything that
// redirects stdin — the REPL's first ReadLine returns null and would take
// the whole surface down with it. The companion UI is the real client
// there, so run until shutdown instead.
if (Console.IsInputRedirected)
{
    Console.WriteLine($"ECI-CAS surface listening. SSE at {string.Join(", ", app.Urls)}/api/stream. No console input — running until shutdown.");
    await app.WaitForShutdownAsync();
    return;
}

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

internal sealed record PerceiveRequest(string Text, string? ProfileId = null);

internal sealed record CreateProfileRequest(string DisplayName, string Avatar);
