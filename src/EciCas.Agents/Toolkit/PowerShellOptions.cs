namespace EciCas.Agents.Toolkit;

/// <summary>
/// PowerShell's own gate, separate from <see cref="ToolkitOptions.Enabled"/>
/// (which is shared by every toolkit and only decides whether the fan-out
/// runs at all). This one is specific to the risk this particular toolkit
/// carries: LLM-translated natural language becomes a script that runs with
/// the full permissions of whoever is running Morrow.
/// </summary>
public sealed class PowerShellOptions
{
    /// <summary>
    /// Off by default, everywhere, on every tier -- unlike
    /// <see cref="ToolkitOptions.Enabled"/> this is never flipped on by a
    /// tier file. The same one-line, human, config-only edit as a manifest's
    /// <c>Approved</c> flag: nothing in code ever sets this to true. Turning
    /// it on is the disclaimer -- the person who edits the file is the one
    /// who read it.
    /// </summary>
    public bool Approved { get; set; }
}
