namespace EciCas.Core;

public interface IArchitectureBoundary
{
    ArchitectureLayer Layer { get; }
    string Responsibility { get; }
}

public abstract class ArchitectureBoundaryBase : IArchitectureBoundary
{
    protected ArchitectureBoundaryBase(ArchitectureLayer layer, string responsibility)
    {
        Layer = layer;
        Responsibility = responsibility;
    }

    public ArchitectureLayer Layer { get; }
    public string Responsibility { get; }
}

public sealed class SharedCoreBoundary : ArchitectureBoundaryBase
{
    public SharedCoreBoundary()
        : base(ArchitectureLayer.SharedCore, "Shared core: bus, agents, prompts, archive contracts, routing, and the memory model.")
    {
    }
}

public sealed class PlatformShellBoundary : ArchitectureBoundaryBase
{
    public PlatformShellBoundary()
        : base(ArchitectureLayer.PlatformShell, "Platform shell: OS/window/input capture, desktop integration, and UI lifecycle.")
    {
    }
}

public sealed class RemoteRelayBoundary : ArchitectureBoundaryBase
{
    public RemoteRelayBoundary()
        : base(ArchitectureLayer.RemoteRelay, "Remote relay: provider auth, secret management, and model gatewaying.")
    {
    }
}

public sealed class SyncLayerBoundary : ArchitectureBoundaryBase
{
    public SyncLayerBoundary()
        : base(ArchitectureLayer.SyncLayer, "Sync layer: Steam Cloud archive/state and durable user memory.")
    {
    }
}
