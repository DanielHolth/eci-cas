namespace EciCas.Core;

/// <summary>
/// Security's gate before Action. Green proceeds. Yellow triggers exactly one
/// Intent revision pass, then proceeds. Red produces a deterministic Blocked
/// notice and Action never runs.
/// </summary>
public enum Verdict
{
    Green,
    Yellow,
    Red,
}
