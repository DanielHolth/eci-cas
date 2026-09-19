using System.Text.Json;
using EciCas.Agents.Toolkit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EciCas.Host.Endpoints;

/// <summary>
/// The roster the Toolkit tab draws: what the catalog holds, and whether the
/// running tier lets any of it run. Read live so the tab cannot list a tool
/// the Host does not have.
/// </summary>
internal static class ToolkitEndpoints
{
    public static void MapToolkits(this WebApplication app, JsonSerializerOptions jsonOptions)
    {
        app.MapGet("/api/toolkits", (IToolkitCatalog catalog, IOptions<ToolkitOptions> toolkit,
            IOptions<SearchOptions> search) =>
        {
            var tiersOn = toolkit.Value.Enabled;
            var items = catalog.All.Select(d => new
            {
                name = d.Name,
                description = d.Description,
                status = !tiersOn ? "disabled"
                    : d.Name == "search" && !search.Value.Enabled ? "disabled"
                    : "ready",
            });
            return Results.Json(items, jsonOptions);
        });
    }
}
