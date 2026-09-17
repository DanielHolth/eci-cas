using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Agents.Utterances;
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
    public static async Task RunAsync(WebApplication app)
    {
        var review = app.Services.GetRequiredService<IArchitectureReviewService>().Review();
        if (review.MissingLayers.Count > 0)
        {
            Console.WriteLine(
                $"Architecture review: missing explicit layer(s): {string.Join(", ", review.MissingLayers.Select(l => l.ToString()))}. This is a planning reminder, not a hard stop.");
        }

        if (review.Findings.Count > 0)
        {
            Console.WriteLine($"Architecture review: {string.Join("; ", review.Findings)}");
        }

        var manifest = app.Services.GetRequiredService<IOptions<RoutingManifest>>().Value;
        RoutingManifest.Validate(manifest, app.Services.GetServices<IAgent>());

        // Cheap re-read of the same cached singletons resolved above, not a re-construction.
        var substrateOptions = app.Services.GetRequiredService<IOptions<SubstrateOptions>>().Value;
        SubstrateManifestValidator.Validate(substrateOptions, app.Services.GetServices<IAgent>(), [SubstrateConsolidator.AgentName, SubstrateFactExtractor.AgentName, FactPicker.AgentName, MaintenanceOptions.RebuildAgentName]);

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
        // chunk-and-pick, and nothing announces it. Run before anything searches,
        // using the embedder this host resolved rather than a path repeated in a
        // launcher script that can disagree with the tier. Free on a warm archive,
        // and silent when it found nothing to do.
        var repaired = await app.Services.GetRequiredService<IArchiveMaintenance>().RunAsync(CancellationToken.None);
        if (repaired.RewrittenFiles > 0)
        {
            Console.WriteLine($"Archive vectors: embedded {repaired.EmbeddedRows} row(s) across {repaired.RewrittenFiles} file(s) with {repaired.ModelId}.");
        }

        // The inverted log's half of the same job: a vector and a thread are
        // derived columns, and derived only means derived if something
        // rebuilds them.
        var backfill = app.Services.GetRequiredService<FactBackfill>();
        var built = await backfill.RunAsync(CancellationToken.None);
        if (built.Extracted > 0 || built.Embedded > 0 || built.Threaded > 0)
        {
            Console.WriteLine($"Fact store: read {built.Extracted} utterance(s), embedded {built.Embedded} row(s), threaded {built.Threaded}.");
        }

        // And after the gaps are filled, the rows a better model owes a
        // second read. Ordered this way so a turn taken while the host was
        // off is indexed first and then re-read once, rather than waiting a
        // boot for the strong pass to notice it.
        await app.Services.GetRequiredService<FactRebuild>().RunAsync(CancellationToken.None);

        PassageCorpus.EnsureModelAgreement(
            await app.Services.GetRequiredService<IPassageStore>().StampedModelsAsync(CancellationToken.None),
            embedder.ModelId);
    }
}
