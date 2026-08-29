namespace EciCas.Substrates;

/// <summary>
/// Budget tiers, as a manifest: which provider ("mock" or "live") backs each
/// logical substrate class. Adding or repricing a tier is a config line, never
/// a code change — see SubstrateRegistry.
/// </summary>
public sealed class BudgetOptions
{
    public Dictionary<string, string> Tiers { get; set; } = [];
}
