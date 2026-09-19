using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using EciCas.Core;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// "Make me a toolkit that ...": drafts a manifest over the registered
/// capabilities and saves it pending. It never approves one -- the draft
/// waits in the Toolkit tab, showing its raw verb, until a person does.
/// A draft that fails validation, carries something shaped like a secret,
/// or would steal routing from an existing toolkit gets one rewrite.
/// </summary>
public sealed partial class ToolsmithCapability(
    ISubstrateProvider substrate,
    IInstructionStore instructions,
    IEmbeddingProvider embeddings,
    IOptions<ToolkitOptions> options) : ICapability
{
    public const string AgentName = "Toolkit.Toolsmith";

    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public string Name => "toolsmith";

    public string Description => "Drafts a new toolkit manifest from the person's description and leaves it pending approval.";

    public CapabilityRisk Risk => CapabilityRisk.Local;

    public async Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        if (call.Catalog is not ManifestCatalog catalog)
        {
            return new ToolkitOutcome(string.Empty, false, "There is no toolkit folder to write to.");
        }

        var usage = new List<ToolkitSubstrateCall>();
        var prompt = InstructionFile.Fill(instructions.For("Toolsmith"),
            ("capabilities", Capabilities(catalog)),
            ("toolkits", string.Join(Environment.NewLine, catalog.Entries.Where(e => e.Manifest is not null).Select(e => $"- {e.Manifest!.Name}: {e.Manifest.Description}"))),
            ("request", call.Command));

        string? warning = null;
        ToolkitManifest? manifest = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reply = await CompleteAsync(prompt, usage, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                return new ToolkitOutcome(string.Empty, false, "Couldn't reach the model that drafts toolkits.", Usage: usage);
            }

            var (draft, refusal, problem) = Parse(reply);
            if (refusal is not null)
            {
                return new ToolkitOutcome($"No toolkit drafted: {refusal}", true, null, Usage: usage);
            }

            var conflict = false;
            if (draft is not null && problem is null)
            {
                problem = Check(draft, catalog);
                if (problem is null && await ConflictAsync(draft, catalog, cancellationToken).ConfigureAwait(false) is { } clash)
                {
                    (problem, conflict) = (clash, true);
                }
            }

            if (problem is null || (conflict && attempt == 1))
            {
                manifest = draft;
                warning = problem;
                break;
            }

            if (attempt == 1)
            {
                return new ToolkitOutcome(string.Empty, false, $"The draft toolkit didn't hold up: {problem}", Usage: usage);
            }

            prompt += Environment.NewLine + Environment.NewLine + reply + Environment.NewLine + Environment.NewLine
                + InstructionFile.Fill(instructions.For("Toolsmith", "retry"), ("problem", problem));
        }

        var saved = manifest! with { Approved = false, Tiers = ["pro", "premium", "mock"] };
        var path = Path.Combine(catalog.Directory, saved.Name + ".json");
        Directory.CreateDirectory(catalog.Directory);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(saved, ManifestCatalog.SerializerOptions), cancellationToken).ConfigureAwait(false);
        catalog.Reload();

        var text = $"Drafted a toolkit called \"{saved.Name}\" ({saved.Description}) using {saved.Verb.Capability}. " +
                   "It won't run until the person approves it in the Toolkit tab.";
        return new ToolkitOutcome(warning is null ? text : text + " Heads up: " + warning, true, null, Usage: usage);
    }

    private string Capabilities(ManifestCatalog catalog) => string.Join(Environment.NewLine, catalog.Capabilities.Values
        .Where(c => c.Risk <= options.Value.MaxRisk && c is not ToolsmithCapability)
        .Select(c => $"- {c.Name}: {c.Description}" + (c.OptionsType is { } type
            ? " Options schema: " + JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, type).ToJsonString()
            : " Takes no options.")));

    private async Task<string?> CompleteAsync(string prompt, List<ToolkitSubstrateCall> usage, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await substrate.CompleteAsync(AgentName, prompt, cancellationToken).ConfigureAwait(false);
            usage.Add(new ToolkitSubstrateCall("draft", result, result.Latency.TotalMilliseconds));
            return result.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            usage.Add(new ToolkitSubstrateCall("draft", null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, SubstrateHealth.Classify(ex)));
            return null;
        }
    }

    private static (ToolkitManifest? Draft, string? Refusal, string? Problem) Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (null, null, "the reply held no JSON object");
        }

        try
        {
            var json = reply[start..(end + 1)];
            if (JsonNode.Parse(json)?["error"]?.GetValue<string>() is { } refusal)
            {
                return (null, refusal, null);
            }

            return (JsonSerializer.Deserialize<ToolkitManifest>(json, ManifestCatalog.SerializerOptions), null, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (null, null, $"the JSON didn't parse: {ex.Message}");
        }
    }

    /// <summary>What the catalog would reject on load, plus the checks only a machine-written draft needs.</summary>
    private static string? Check(ToolkitManifest draft, ManifestCatalog catalog)
    {
        if (catalog.Validate(draft, out _) is { } invalid)
        {
            return invalid;
        }

        if (!Slug().IsMatch(draft.Name))
        {
            return "name must be lowercase letters, digits and dashes";
        }

        if (draft.Verb.Capability.Equals("toolsmith", StringComparison.OrdinalIgnoreCase))
        {
            return "a toolkit cannot use the toolsmith capability";
        }

        // Only a pending draft of the same name may be replaced; anything a
        // person approved, or that another file already claims, is theirs.
        if (catalog.Entries.FirstOrDefault(e => e.Manifest?.Name.Equals(draft.Name, StringComparison.OrdinalIgnoreCase) == true) is { } taken
            && (taken.Status != ManifestStatus.Pending || taken.Pack is not null || !taken.File.Equals(draft.Name + ".json", StringComparison.OrdinalIgnoreCase)))
        {
            return $"a toolkit named \"{draft.Name}\" already exists";
        }

        var options = draft.Verb.Options?.GetRawText() ?? string.Empty;
        return Secret().IsMatch(options) ? "the options contain something that looks like a password or token; name an environment variable instead" : null;
    }

    /// <summary>
    /// Each new trigger, embedded the way a turn is, must sit closer to its
    /// own siblings than to any existing toolkit by the routing margin --
    /// otherwise it would take turns that route elsewhere today.
    /// </summary>
    private async Task<string?> ConflictAsync(ToolkitManifest draft, ManifestCatalog catalog, CancellationToken cancellationToken)
    {
        var others = catalog.All.Where(d => !d.Name.Equals(draft.Name, StringComparison.OrdinalIgnoreCase) && d.Triggers.Count > 0).ToList();
        if (others.Count == 0 || draft.Triggers.Count < 2)
        {
            return null;
        }

        try
        {
            var asked = await embeddings.EmbedAsync(draft.Triggers, EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
            var own = await embeddings.EmbedAsync(draft.Triggers, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
            var theirs = new List<(string Name, float[] Vector)>();
            foreach (var other in others)
            {
                var vectors = await embeddings.EmbedAsync(other.Triggers, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
                theirs.AddRange(vectors.Select(v => (other.Name, v)));
            }

            var clashes = new List<string>();
            for (var i = 0; i < asked.Count; i++)
            {
                if (asked[i].Length == 0)
                {
                    return null;
                }

                var sibling = own.Where((_, j) => j != i).Max(v => Dot(asked[i], v));
                var (name, score) = theirs.Select(t => (t.Name, Dot(asked[i], t.Vector))).MaxBy(t => t.Item2);
                if (score >= sibling - options.Value.RouteMargin)
                {
                    clashes.Add($"\"{draft.Triggers[i]}\" sounds like the {name} toolkit");
                }
            }

            return clashes.Count == 0 ? null : "some triggers would route to other toolkits: " + string.Join("; ", clashes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        double dot = 0;
        for (var k = 0; k < Math.Min(a.Length, b.Length); k++)
        {
            dot += a[k] * b[k];
        }

        return dot;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"(?i)(sk-[a-z0-9]{8,}|bearer\s+[a-z0-9._-]{8,}|(api[_-]?key|token|secret|password)=[^&""\s]{4,}|\b(?=[a-z_-]*\d)[a-z0-9_-]{32,})")]
    private static partial Regex Secret();
}
