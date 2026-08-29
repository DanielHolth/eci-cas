using EciCas.Agents.Action;
using EciCas.Agents.Governance;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<GovernanceOptions>(builder.Configuration.GetSection("Governance"));
builder.Services.Configure<RoutingManifest>(builder.Configuration.GetSection("RoutingManifest"));

builder.Services.AddSingleton<BusActivityTracker>();
builder.Services.AddSingleton<IMessageBus, ChannelBus>();

RegisterAgent<PerceptionAgent>(builder.Services);
RegisterAgent<GovernanceAgent>(builder.Services);
RegisterAgent<IntentAgent>(builder.Services);
RegisterAgent<SecurityAgent>(builder.Services);
RegisterAgent<ActionAgent>(builder.Services);
RegisterAgent<ArchiveLogger>(builder.Services);
RegisterAgent<ConsoleSubscriber>(builder.Services);

var app = builder.Build();

var manifest = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingManifest>>().Value;
RoutingManifest.Validate(manifest, app.Services.GetServices<IAgent>());

await app.StartAsync();

var perception = app.Services.GetRequiredService<PerceptionAgent>();
var activity = app.Services.GetRequiredService<BusActivityTracker>();

Console.WriteLine("ECI-CAS walking skeleton. Type a prompt (empty line to exit).");
string? line;
while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
{
    perception.Perceive(line);
    await activity.WhenIdleAsync(TimeSpan.FromSeconds(10));
}

await app.StopAsync();

static void RegisterAgent<TAgent>(IServiceCollection services) where TAgent : AgentBase, IAgent
{
    services.AddSingleton<TAgent>();
    services.AddSingleton<IAgent>(sp => sp.GetRequiredService<TAgent>());
    services.AddHostedService(sp => sp.GetRequiredService<TAgent>());
}
