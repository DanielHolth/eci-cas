using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EciCas.Agents.Recall;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Endpoints;

/// <summary>
/// The Debug panel's surface: read the live knobs, set them, and write them
/// back into the tier file they came from. Lives here rather than in
/// Program so the host's startup reads as a list of what it maps.
/// </summary>
internal static class KnobsEndpoints
{
    public static void MapKnobs(this WebApplication app, JsonSerializerOptions jsonOptions, int warmupBudgetMs)
    {
        // The Debug panel's sliders — live, in-memory, and reset on restart. Read
        // on every Intent prompt, so a drag takes effect on the very next turn.
        app.MapGet("/api/knobs", (RuntimeKnobs knobs, TierCatalog tiers, IOptions<RecallOptions> recall, IOptions<KnobDefaults> knobDefaults) =>
            Results.Json(ToKnobsPayload(knobs, tiers, recall.Value, knobDefaults.Value), jsonOptions));

        app.MapPost("/api/knobs", (KnobsRequest request, RuntimeKnobs knobs, TierCatalog tiers, IOptions<RecallOptions> recall, IOptions<KnobDefaults> knobDefaults,
            ISubstrateProvider substrates, IOptions<SubstrateOptions> substrateConfig) =>
        {
            // First, because it re-seeds RecallDepth: a request that sets both
            // should end with the explicit depth, not with the tier's answer to it.
            if (request.Tier is { } tierName)
            {
                if (!tiers.Switch(tierName))
                {
                    return Results.BadRequest($"No such tier '{tierName}'.");
                }

                // A swap points the agents at models this process may never have
                // called. Boot warms the tier it started on; without this, going
                // Mock -> Default made the next turn pay the cold handshake, or on
                // local the whole weight load, exactly as a cold boot would -- and
                // that first slow call is the one that used to time out.
                //
                // Not awaited: the switch has already taken effect, so the surface
                // has its answer, and a POST that blocked for a 4B loading off disk
                // would look like a hung slider. Errors are SubstrateWarmup's own
                // business; it cannot throw.
                _ = SubstrateWarmup.RunAsync(
                    substrates, substrateConfig.Value, TimeSpan.FromMilliseconds(warmupBudgetMs), Console.WriteLine, CancellationToken.None);
            }

            if (request.MaxSentences is { } n)
            {
                knobs.MaxSentences = n;
            }

            if (request.ReflectionEvery is { } r)
            {
                knobs.ReflectionEvery = r;
            }

            if (request.PerceptionChars is { } p)
            {
                knobs.PerceptionChars = p;
            }

            if (request.ContextTurns is { } c)
            {
                knobs.ContextTurns = c;
            }

            if (request.RecallDepth is { } d)
            {
                knobs.RecallDepth = d;
            }

            if (request.RecallThreads is { } t)
            {
                knobs.RecallThreads = t;
            }

            if (request.Mood is { } moodName && Enum.TryParse<Mood>(moodName, ignoreCase: true, out var mood))
            {
                knobs.Mood = mood;
            }

            return Results.Json(ToKnobsPayload(knobs, tiers, recall.Value, knobDefaults.Value), jsonOptions);
        });

        // Writes every live knob back into the active tier's file, so a setting
        // found by dragging survives the restart that found it. Both copies get it:
        // the source tree's file is the one a human and git read, and the one under
        // the binary is the one the next boot actually loads -- writing only the
        // source means the next run silently ignores the save, and writing only the
        // build output means the next `dotnet build` silently reverts it.
        //
        // Read-modify-write of the parsed JSON rather than a re-serialise of
        // RecallOptions/KnobDefaults: a tier file carries Classes, Agents and Rank
        // too, and nothing here has any business rewriting those.
        app.MapPost("/api/knobs/save", (RuntimeKnobs knobs, TierCatalog tiers, IOptions<RecallOptions> recall, IOptions<KnobDefaults> knobDefaults) =>
        {
            var file = $"appsettings.{tiers.Active}.json";
            var targets = new[]
            {
                Path.Combine(AppContext.BaseDirectory, file),
                Path.Combine(SourceTierDirectory(), file),
            };

            var written = new List<string>();
            foreach (var path in targets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                if (!TryWriteNumber(ref text, "MaxPickedPerWorker", knobs.RecallDepth)
                    || !TryWriteNumber(ref text, "Threads", knobs.RecallThreads)
                    || !TryWriteNumber(ref text, "MaxSentences", knobs.MaxSentences)
                    || !TryWriteNumber(ref text, "ReflectionEvery", knobs.ReflectionEvery)
                    || !TryWriteNumber(ref text, "PerceptionChars", knobs.PerceptionChars)
                    || !TryWriteNumber(ref text, "ContextTurns", knobs.ContextTurns)
                    || !TryWriteString(ref text, "Mood", knobs.Mood.ToString()))
                {
                    return Results.Problem($"{file} is missing one of Recall:MaxPickedPerWorker, Recall:Threads, Knobs:MaxSentences, Knobs:ReflectionEvery, Knobs:PerceptionChars, Knobs:ContextTurns, Knobs:Mood.");
                }

                // Parsed to prove the edit, not to produce it. Round-tripping through
                // JsonNode reformatted the whole file -- every one-line object in it
                // exploded to eight lines and the trailing newline went -- which
                // turned a two-number save into a diff nobody can read. Editing the
                // two numbers in place leaves the file exactly as its author wrote
                // it, and this parse is what keeps that from being a licence to emit
                // broken JSON.
                JsonNode.Parse(text);

                File.WriteAllText(path, text);
                written.Add(path);
            }

            if (written.Count == 0)
            {
                return Results.NotFound($"No {file} to write -- {tiers.Active} has no tier file on disk.");
            }

            // The bound options are what the payload reports as "saved", so they have
            // to move with the file or the Save button stays lit after a good save.
            recall.Value.MaxPickedPerWorker = knobs.RecallDepth;
            recall.Value.Threads = knobs.RecallThreads;
            knobDefaults.Value.MaxSentences = knobs.MaxSentences;
            knobDefaults.Value.ReflectionEvery = knobs.ReflectionEvery;
            knobDefaults.Value.PerceptionChars = knobs.PerceptionChars;
            knobDefaults.Value.ContextTurns = knobs.ContextTurns;
            knobDefaults.Value.Mood = knobs.Mood;

            return Results.Json(ToKnobsPayload(knobs, tiers, recall.Value, knobDefaults.Value), jsonOptions);
        });

        static object ToKnobsPayload(RuntimeKnobs knobs, TierCatalog tiers, RecallOptions recall, KnobDefaults knobDefaults) => new
        {
            tier = tiers.Active,
            tiers = tiers.Presets.Select(p => new { name = p.Name, missingKeys = p.MissingKeys }),
            maxSentences = knobs.MaxSentences,
            reflectionEvery = knobs.ReflectionEvery,
            perceptionChars = knobs.PerceptionChars,
            contextTurns = knobs.ContextTurns,
            recallDepth = knobs.RecallDepth,
            recallThreads = knobs.RecallThreads,
            // What the tier file on disk says, so the surface can grey its Save
            // button rather than having to guess whether a drag is unsaved.
            savedRecallDepth = recall.MaxPickedPerWorker,
            savedRecallThreads = recall.Threads,
            savedMaxSentences = knobDefaults.MaxSentences,
            savedReflectionEvery = knobDefaults.ReflectionEvery,
            savedPerceptionChars = knobDefaults.PerceptionChars,
            savedContextTurns = knobDefaults.ContextTurns,
            savedMood = knobDefaults.Mood.ToString(),
            mood = knobs.Mood.ToString(),
            moods = Enum.GetNames<Mood>(),
        };
    }

    /// <summary>
    /// Replaces one numeric JSON property's value in place, leaving every byte
    /// around it alone. Returns false if the key is not there, which the caller
    /// treats as a refusal rather than an invitation to add it: a tier file that
    /// does not mention a knob is a tier file whose author left it to the base
    /// layer, and quietly writing one in would change what the tier means.
    /// </summary>
    static bool TryWriteNumber(ref string json, string key, int value)
    {
        var pattern = new Regex($@"(""{Regex.Escape(key)}""\s*:\s*)-?\d+");
        if (!pattern.IsMatch(json))
        {
            return false;
        }

        json = pattern.Replace(json, m => m.Groups[1].Value + value.ToString(CultureInfo.InvariantCulture), 1);
        return true;
    }

    /// <summary>Same trade as <see cref="TryWriteNumber"/>, for a quoted string value.</summary>
    static bool TryWriteString(ref string json, string key, string value)
    {
        var pattern = new Regex($@"(""{Regex.Escape(key)}""\s*:\s*)""[^""]*""");
        if (!pattern.IsMatch(json))
        {
            return false;
        }

        json = pattern.Replace(json, m => m.Groups[1].Value + "\"" + value + "\"", 1);
        return true;
    }

    /// <summary>
    /// The tier files in the source tree, walking up from the binary until a
    /// directory holding appsettings.json is found. The build copies those files
    /// next to the binary, so a save that touched only the copy would be undone
    /// by the next build -- and there is no configuration entry for "where did
    /// this come from", only the layout.
    /// </summary>
    static string SourceTierDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "appsettings.json");
            if (File.Exists(candidate) && !string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}

internal sealed record KnobsRequest(int? MaxSentences = null, int? ReflectionEvery = null, int? PerceptionChars = null, int? ContextTurns = null, int? RecallDepth = null, int? RecallThreads = null, string? Mood = null, string? Tier = null);
