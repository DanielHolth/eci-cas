using EciCas.Agents.Identity;
using EciCas.Agents.Librarian;
using EciCas.Agents.Recall;
using EciCas.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Startup;

/// <summary>
/// The live layer: what an operator can move at runtime, and the catalog of
/// tiers those values can be replaced wholesale from. Separate from
/// OptionsRegistration because these are not bindings — every one of them is
/// seeded from a bound option and then diverges from it, which is exactly
/// what the Debug panel's Save button is for.
/// </summary>
internal static class KnobRegistration
{
    public static IServiceCollection AddKnobs(this WebApplicationBuilder builder, string tier)
    {
        var services = builder.Services;

        services.Configure<PersonaNameOptions>(builder.Configuration.GetSection("Identity"));
        services.AddSingleton<PersonaName>();

        services.AddSingleton(sp => new TierCatalog(
            TierCatalogLoader.Load(AppContext.BaseDirectory),
            sp.GetRequiredService<IOptions<SubstrateOptions>>().Value,
            sp.GetRequiredService<IOptions<RecallOptions>>().Value,
            sp.GetRequiredService<IOptions<LibrarianOptions>>().Value,
            sp.GetRequiredService<RuntimeKnobs>(),
            sp.GetRequiredService<IOptions<KnobDefaults>>().Value,
            tier));

        // RecallDepth is a live knob, but its starting value is configuration, not a
        // constant: RecallOptions.MaxPickedPerWorker is the tier's answer to "how many
        // rows may one picking call hand back". Seeding it here is what makes that
        // option mean anything -- until this, every tier ran at the knob's hardcoded 5
        // whatever it configured, and the field was read by nothing at all.
        services.AddSingleton(sp => new RuntimeKnobs
        {
            RecallDepth = sp.GetRequiredService<IOptions<RecallOptions>>().Value.MaxPickedPerWorker,
            MaxSentences = sp.GetRequiredService<IOptions<KnobDefaults>>().Value.MaxSentences,
            ReflectionEvery = sp.GetRequiredService<IOptions<KnobDefaults>>().Value.ReflectionEvery,
            PerceptionChars = sp.GetRequiredService<IOptions<KnobDefaults>>().Value.PerceptionChars,
            ContextTurns = sp.GetRequiredService<IOptions<KnobDefaults>>().Value.ContextTurns,
            Mood = sp.GetRequiredService<IOptions<KnobDefaults>>().Value.Mood,
        });

        return services;
    }
}
