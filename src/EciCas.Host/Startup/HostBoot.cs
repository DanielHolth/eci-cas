using System.Text.Json;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Host.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// The running order, from arguments to a listening host with warm models.
///
/// Public and separate from Program because there are now two front ends over
/// the same substrate: the console host, which follows this with a REPL, and
/// the desktop shell, which follows it with a window. Neither is the real one
/// — what they have in common is everything up to the point where a person
/// can type, and that is exactly what this returns.
/// </summary>
public static class HostBoot
{
    /// <summary>
    /// Builds, boot-checks, maps, starts and warms. The returned app is
    /// listening; the caller owns what happens next and owns disposing it.
    /// </summary>
    public static async Task<WebApplication> StartAsync(string[] args)
    {
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

        await builder.AddStoresAsync();

        builder.AddKnobs(tier);
        builder.Services.AddToolkits();
        builder.Services.AddAgents();

        var app = builder.Build();

        await BootChecks.RunAsync(app);

        app.UseCors(surface.CorsPolicy);

        // Before the endpoints, so a file named like a route cannot shadow one,
        // and only when there is something to serve — see HostSurface.ClientPath.
        if (surface.ClientPath is { } clientPath)
        {
            var files = new PhysicalFileProvider(clientPath);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,

                // The HTML is never cached; everything beside it is cached
                // forever. Next names its chunks by content hash, so the only
                // file whose contents change under a fixed name is the entry
                // document -- and a WebView2 holding yesterday's copy of that
                // one file serves yesterday's whole surface from a build that
                // is on disk and correct, which is an afternoon lost to
                // looking for a bug in the wrong half of the program.
                OnPrepareResponse = context =>
                    context.Context.Response.Headers.CacheControl =
                        context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                            ? "no-cache"
                            : "public, max-age=31536000, immutable",
            });
        }

        var jsonOptions = app.Services.GetRequiredService<JsonSerializerOptions>();

        app.MapPersona(jsonOptions);
        app.MapKnobs(jsonOptions, surface.WarmupBudgetMs);
        app.MapVitals(jsonOptions);
        app.MapFacts(jsonOptions);
        app.MapStreams(jsonOptions);

        await app.StartAsync();

        // Before anyone can type, not after: the point is that the first thing a
        // person says does not pay for the model load. Configurable because a
        // mock-only or vendor-only tier wants far less of a budget than a local 2B
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

        return app;
    }
}
