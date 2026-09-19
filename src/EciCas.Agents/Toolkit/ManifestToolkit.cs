namespace EciCas.Agents.Toolkit;

/// <summary>
/// One approved manifest bound to its capability and parsed options. Only
/// <see cref="ManifestCatalog"/> builds these, and only for a manifest that
/// validated, is approved, and passed the tier's gate.
/// </summary>
public sealed class ManifestToolkit(ToolkitManifest manifest, ICapability capability, object? options, IToolkitCatalog catalog) : IToolkit
{
    public string Name => manifest.Name;

    public ICapability Capability => capability;

    public Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken) =>
        capability.ExecuteAsync(new CapabilityCall(command, options, catalog), cancellationToken);
}
