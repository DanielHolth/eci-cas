using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// The instruction files the host actually ships, not stand-ins. A test
/// double here would let a hand revision break every agent while the suite
/// stayed green — the files are linked into the test output precisely so
/// that loading them is itself a check on what is in them.
/// </summary>
public static class ShippedInstructions
{
    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "instructions");

    public static readonly IInstructionStore Store =
        new FileInstructionStore(Directory);
}
