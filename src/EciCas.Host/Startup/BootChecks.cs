using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// Everything checked or repaired between building the host and starting
/// it. Two kinds live here on purpose: the refusals -- routing and substrate
/// drift, a passage corpus written by a different embedder -- and the quiet
/// repairs, which is the archive backfill and the recency trim.
///
/// This is where the recovery agent in docs/roadmap.md grows from. It
/// already has the shape: run before any agent consumes, say what it found,
/// and only refuse for the things a running host cannot survive.
/// </summary>
internal static class BootChecks
{
    public static async Task RunAsync(WebApplication app, HostStores stores)
    {
        var archiveStore = stores.Archive;
        var archiveDirectory = stores.ArchiveDirectory;

        var manifest = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingManifest>>().Value;
        RoutingManifest.Validate(manifest, app.Services.GetServices<IAgent>());

        // Cheap re-read of the same cached singletons resolved above, not a re-construction.
        var substrateOptions = app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value;
        SubstrateManifestValidator.Validate(substrateOptions, app.Services.GetServices<IAgent>());

        // Every tier, bound but not applied — see TierCatalog for why a live switch
        // is a few reference writes rather than a rebuild. Registered against the
        // same singletons validated above, which is the point: switching mutates
        // what every agent already holds.
        var tiers = app.Services.GetRequiredService<TierCatalog>();
        Console.WriteLine($"Tiers loadable live: {string.Join(", ", tiers.Presets.Select(p => p.MissingKeys.Count == 0 ? p.Name : $"{p.Name} (needs {string.Join('+', p.MissingKeys)})"))}");

        // A model swap is the one event that can take a note away, and it does it
        // without a log line — so it is checked here, before anything searches.
        var embedder = app.Services.GetRequiredService<IEmbeddingProvider>();

        // Rows the embedder never saw make their whole pair fall back to
        // chunk-and-pick, and nothing announces it. The fix is a maintenance job in
        // the ordinary sense — run it at boot, before anything searches, using the
        // embedder this host resolved rather than a path repeated in a launcher
        // script that can disagree with the tier. Free on a warm archive.
        var backfilled = await ArchiveBackfill.RunAsync(archiveDirectory, embedder, onFile: null, CancellationToken.None);
        if (backfilled.Files > 0)
        {
            // The store has been reading these same files; what it cached before the
            // rewrite is now the stale copy.
            archiveStore.Invalidate();
            Console.WriteLine($"Archive vectors: embedded {backfilled.Rows} row(s) across {backfilled.Files} file(s) with {embedder.ModelId}.");
        }

        // The recency lane reaches back a year, and the trim is a boot-time job so
        // no turn pays for it: a write appends, and only a restart drops what has
        // aged out. Nothing is lost by it - the lane is a view of rows the pair
        // files still hold. After the backfill, so the lane it caches is the
        // vectored one.
        await archiveStore.TrimRecentAsync(CancellationToken.None);

        PassageCorpus.EnsureModelAgreement(
            await app.Services.GetRequiredService<IPassageStore>().StampedModelsAsync(CancellationToken.None),
            embedder.ModelId);
    }
}
