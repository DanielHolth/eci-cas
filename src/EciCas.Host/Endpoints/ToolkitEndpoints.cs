using System.Text.Json;
using System.Text.Json.Nodes;
using EciCas.Agents.Toolkit;
using EciCas.Host.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Endpoints;

/// <summary>
/// The roster the Toolkit tab draws, read live from the catalog: every
/// manifest on disk with its status, and for a pending one the raw verb, so
/// the person approves what will run rather than what it says about itself.
/// Approve is the only code path that sets <c>approved</c>, and it only ever
/// runs off a click.
/// </summary>
internal static class ToolkitEndpoints
{
    public static void MapToolkits(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        app.MapGet("/api/toolkits", (ManifestCatalog catalog, IOptions<SearchOptions> search) =>
        {
            var items = catalog.Entries.Select(e => new
            {
                file = e.File,
                pack = e.Pack,
                name = e.Manifest?.Name ?? e.File,
                description = e.Manifest?.Description,
                capability = e.Manifest?.Verb?.Capability,
                risk = e.Manifest?.Verb?.Capability is { } c && catalog.Capabilities.TryGetValue(c, out var capability) ? capability.Risk.ToString() : null,
                options = e.Manifest?.Verb?.Options,
                status = e.Status == ManifestStatus.Ready && e.Manifest!.Verb.Capability == "web_search" && !search.Value.Enabled
                    ? "disabled"
                    : e.Status.ToString().ToLowerInvariant(),
                reason = e.Reason,
            });
            return Results.Json(items, jsonOptions);
        });

        app.MapPost("/api/toolkits/{file}/approve", (string file, ManifestCatalog catalog) =>
            Pending(catalog, file) is not { } path
                ? Results.NotFound()
                : Rewrite(path, catalog, node => node["approved"] = true));

        app.MapPost("/api/toolkits/{file}/discard", (string file, ManifestCatalog catalog) =>
        {
            if (Pending(catalog, file) is not { } path)
            {
                return Results.NotFound();
            }

            File.Delete(path);
            catalog.Reload();
            return Results.NoContent();
        });

        app.MapGet("/api/packs", (IConfiguration configuration) =>
        {
            var safeMode = Packs.SafeMode(configuration);
            var items = Packs.Scan(_ => { }).Select(p => new
            {
                name = p.Name,
                approved = p.Approved,
                active = p.Approved && !safeMode,
                permissions = p.Permissions ?? [],
                theme = p.Approved && !safeMode ? p.Contributes?.Theme : null,
            });
            return Results.Json(new { safeMode, packs = items }, jsonOptions);
        });
    }

    /// <summary>Only a pending file in the person's own toolkit folder; a pack's manifests ride on the pack's approval.</summary>
    private static string? Pending(ManifestCatalog catalog, string file) =>
        catalog.Entries.Any(e => e.Pack is null && e.Status == ManifestStatus.Pending && e.File.Equals(file, StringComparison.OrdinalIgnoreCase))
            ? Path.Combine(catalog.Directory, Path.GetFileName(file))
            : null;

    private static IResult Rewrite(string path, ManifestCatalog catalog, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString(ManifestCatalog.SerializerOptions));
        catalog.Reload();
        return Results.NoContent();
    }
}
