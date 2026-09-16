using System.Text.Json;

namespace EciCas.Agents.Toolkit;

/// <summary>Which manifests a directory scan produced, split by the one gate that matters.</summary>
/// <param name="Approved">Loads as a callable toolkit this run.</param>
/// <param name="Pending">Parsed and valid, but <c>Approved</c> is false or absent -- reported, never registered.</param>
/// <param name="Invalid">File name paired with why it didn't parse or didn't validate -- reported, never registered.</param>
public sealed record ManifestScanResult(
    IReadOnlyList<ToolkitManifest> Approved,
    IReadOnlyList<string> Pending,
    IReadOnlyList<(string File, string Reason)> Invalid);

/// <summary>
/// Reads every <c>*.json</c> file in the toolkits directory as a
/// <see cref="ToolkitManifest"/>. This is the load-time half of the human-in
/// -the-loop gate: a file appearing in the directory is necessary but not
/// sufficient for it to become a running toolkit, because <c>Approved</c>
/// defaults to false and nothing in this class ever sets it. Dropping a file
/// here (from a future import/Workshop-subscribe flow, or just hand-authored
/// today) only ever gets you to "pending" -- someone has to open it and flip
/// the flag before ToolkitRegistration will wire it up.
/// </summary>
public static class ManifestToolkitLoader
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ManifestScanResult Scan(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new ManifestScanResult([], [], []);
        }

        var approved = new List<ToolkitManifest>();
        var pending = new List<string>();
        var invalid = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            ToolkitManifest? manifest;
            try
            {
                var json = File.ReadAllText(file);
                manifest = JsonSerializer.Deserialize<ToolkitManifest>(json, SerializerOptions);
            }
            catch (Exception failure)
            {
                invalid.Add((fileName, $"couldn't parse: {failure.Message}"));
                continue;
            }

            if (manifest is null)
            {
                invalid.Add((fileName, "file is empty or null"));
                continue;
            }

            var validationError = Validate(manifest);
            if (validationError is not null)
            {
                invalid.Add((fileName, validationError));
                continue;
            }

            if (!manifest.Approved)
            {
                pending.Add(fileName);
                continue;
            }

            approved.Add(manifest);
        }

        return new ManifestScanResult(approved, pending, invalid);
    }

    private static string? Validate(ToolkitManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return "missing \"name\"";
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            return "missing \"description\"";
        }

        if (manifest.Triggers is not { Count: > 0 })
        {
            return "needs at least one entry in \"triggers\"";
        }

        return manifest.Verb.Kind switch
        {
            "http_call" => string.IsNullOrWhiteSpace(manifest.Verb.Url)
                ? "http_call verb needs a \"url\""
                : null,
            "speak_text" => null,
            null or "" => "verb needs a \"kind\"",
            var other => $"unknown verb kind \"{other}\" -- only http_call and speak_text are supported",
        };
    }
}
