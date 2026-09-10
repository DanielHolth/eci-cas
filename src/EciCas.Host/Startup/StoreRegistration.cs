using EciCas.Agents.Identity;
using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Agents.Security;
using EciCas.Core;
using Microsoft.Extensions.Configuration;
using EciCas.Agents.Utterances;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace EciCas.Host.Startup;

/// <summary>
/// Everything the persona remembers with, and the two seeds that only ever
/// run into an empty one. Async because seeding writes: a store that is
/// empty at registration time and filled at first use is a store whose
/// first turn races the seed.
/// </summary>
internal static class StoreRegistration
{
    public static async Task AddStoresAsync(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        var securityRulesPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Security:RulesPath"] ?? "config/security-rules.json");
        services.AddSingleton(SecurityRuleSet.Load(securityRulesPath));

        // Agent state, not the archive: a JSONL side-store the Identity persona
        // entry lives in. It sat under "Archive:" for long enough that an
        // existing local override may still say so — that key is still read.
        var agentStatePath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["AgentState:Path"] ?? builder.Configuration["Archive:Path"] ?? "memory.jsonl");
        var agentStateStore = new JsonlAgentStateStore(agentStatePath);
        services.AddSingleton<IAgentStateStore>(agentStateStore);

        // Instructions before anything that reads one. Files rather than
        // appsettings, because paragraph prose inside a JSON string means escaped
        // newlines, no wrapping and a syntax error one stray quote away. Loaded
        // eagerly so a missing file or a mistyped placeholder stops the host here,
        // where it is one message, rather than degrading a single agent quietly at
        // turn time.
        var instructionStore = new FileInstructionStore(
            Path.Combine(AppContext.BaseDirectory, builder.Configuration["Instructions:Directory"] ?? "instructions"));

        // Who the persona starts as lives in instructions/identity.txt; who it has
        // become lives in the state store, and the store wins. Seeded only when the
        // store has nothing there, so a persona that has grown past the file — by
        // hand, or later by the persona itself — is never overwritten by a redeploy.
        //
        // Which one is live is printed, because the asymmetry bites in one
        // direction: the file is the visible artefact and the store is a line in a
        // JSONL nobody opens, so an edit to the file that changed nothing looks
        // exactly like an edit that worked. It went unnoticed for months once.
        //
        // "Identity:Profile" picks which section of that file seeds a new persona.
        // Resolved here rather than at first use so a name that matches no section
        // stops the host with the list of real ones, instead of surfacing turns
        // later as a persona that sounds subtly wrong.
        var identityProfile = builder.Configuration["Identity:Profile"];
        var identitySection = string.IsNullOrWhiteSpace(identityProfile)
            ? InstructionFile.MainSection
            : identityProfile.Trim();

        var storedIdentity = await agentStateStore.LookupAsync([IdentityAgent.IdentityPath], maxPerPath: 1, CancellationToken.None);
        if (storedIdentity.Count == 0)
        {
            await agentStateStore.WriteAsync(
                [new AgentStateRecord(IdentityAgent.IdentityPath, instructionStore.For("Identity", identitySection), DateTimeOffset.UtcNow, ArchiveDomain.Internal)],
                CancellationToken.None);
            Console.WriteLine($"Identity seeded from {Path.Combine(instructionStore.Directory, "identity.txt")} " +
                $"(profile '{identitySection}').");
        }
        else
        {
            Console.WriteLine($"Identity read from {agentStatePath} (stored {storedIdentity[0].Timestamp:yyyy-MM-dd}); " +
                "instructions/identity.txt seeds a new persona only. Delete that entry to re-seed from the file.");
        }


        var archiveDirectory = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Archive:Directory"] ?? "archive");
        // One record, not a migration: an empty archive gets the one fact that is
        // true before anything has been said, which is what build is running.
        //
        // Notably NOT its own name. A persona that boots already knowing what it is
        // called cannot be introduced to anyone and cannot be renamed either — the
        // seeded fact outranks the conversation, so being told a different name read
        // as a fact about the stranger rather than about itself. It starts nameless,
        // is told a name like anything else it is told, and keeps it only if
        // Archivist judges it worth writing down. That is the whole point of having
        // an Archivist, and it was never once exercised while the name was a seed.
        var seedNeeded = !File.Exists(ParquetArchiveStore.PairPathFor(archiveDirectory, new ArchivePair("assistant", "system")));
        var archiveStore = new ParquetArchiveStore(archiveDirectory,
            builder.Configuration.GetSection("Archive:SharedCategories").Get<string[]>());
        if (seedNeeded)
        {
            var seedRecord = new ArchiveRecord(
                Category: "assistant", Topic: "system", Subtopic: "eci", Subject: "this", Key: "version", Value: "0.1",
                Timestamp: DateTimeOffset.UtcNow, Domain: ArchiveDomain.External, Importance: 0.5);
            await archiveStore.WriteAsync([seedRecord], profileId: null, CancellationToken.None);
        }

        // Wrapped, not replaced: the parquet store still owns the files, and the
        // decorator only stamps a vector on rows on their way into it. Registered as
        // a factory so it picks up whichever embedder the tier configured - on the
        // offline tier that is the null provider, the wrapper writes straight
        // through, and rows land exactly as they did before vectors existed.
        services.AddSingleton<IArchiveStore>(sp =>
            new EmbeddingArchiveStore(archiveStore, sp.GetRequiredService<IEmbeddingProvider>()));

        // Built above, because the persona seed reads from it. Registered here so
        // every agent gets the same loaded instance.
        services.AddSingleton<IInstructionStore>(instructionStore);

        // The passage corpus lives beside the pair files, in the shared tier only —
        // a self-critique belongs to the persona the way the "assistant" category
        // already does.
        services.AddSingleton<IPassageStore>(new ParquetPassageStore(archiveDirectory));

        // The inverted archive: an append-only log of what was said, beside
        // the pair files rather than instead of them. Registered
        // unconditionally and cheap when unused -- it reads nothing until
        // something asks -- so the flag that swaps the agents does not also
        // have to swap the storage.
        services.AddSingleton<IUtteranceLog>(new ParquetUtteranceLog(archiveDirectory));

        // The index beside it: facts, one claim per row, every one of them
        // derived from an utterance and rebuildable from it. Separate files
        // on purpose -- this store is rewritten constantly (vectors, threads,
        // hit counts) and the one above is only ever appended to.
        services.AddSingleton<IFactLog>(new ParquetFactLog(archiveDirectory));
        services.AddSingleton<FactConsult>();
        services.AddSingleton<FactPicker>();
        services.AddSingleton<ThreadWeaver>();
        services.AddSingleton<FactBackfill>();

        // Two gated model calls, both on the write path, both off by default
        // and both disposable by construction.
        //
        // The extractor reads a turn into standalone facts. Without it an
        // utterance is its own single fact, which is how the archive behaved
        // before the split and is wrong in one direction only: long input
        // indexes badly, and re-reading it later fixes it.
        services.AddSingleton<IFactExtractor>(sp =>
            sp.GetRequiredService<IOptions<UtteranceOptions>>().Value.ExtractorEnabled
                ? ActivatorUtilities.CreateInstance<SubstrateFactExtractor>(sp)
                : new VerbatimFactExtractor());

        // The consolidator files a new fact against the ones near it. With
        // Utterances:ConsolidatorEnabled false the null implementation stands
        // in and every candidate set resolves to a new thread -- a deliberate
        // split, because a wrong merge is the error read time cannot undo.
        services.AddSingleton<IFactConsolidator>(sp =>
            sp.GetRequiredService<IOptions<UtteranceOptions>>().Value.ConsolidatorEnabled
                ? ActivatorUtilities.CreateInstance<SubstrateConsolidator>(sp)
                : new NullFactConsolidator());


        // Profiles live beside the archive they scope — one directory per person
        // under archive/profiles/. A surface concern, not a bus citizen.
        services.AddSingleton(new ProfileStore(archiveDirectory));

        // Upkeep as a service rather than a handle handed back out of band: the
        // pass needs the concrete store and the directory, and this is the last
        // place that has both. Everyone else asks for the interface.
        services.AddSingleton<IArchiveMaintenance>(sp =>
            new ParquetArchiveMaintenance(archiveStore, archiveDirectory, sp.GetRequiredService<IEmbeddingProvider>()));
    }
}
