namespace EciCas.Agents.Reflection;

/// <summary>
/// Local loop guard — see plan §3.6. Idea -> arc -> conclusion -> idea would
/// otherwise loop forever while spending on LLM calls, and hop_count can't
/// catch it since each idea is a legitimately new event. Enforced entirely
/// inside Reflection so Governance never grows a fourth job.
/// </summary>
public sealed class ReflectionOptions
{
    public int MaxIdeaGeneration { get; set; } = 1;
}
