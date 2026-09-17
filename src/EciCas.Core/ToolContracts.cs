namespace EciCas.Core;

public sealed record ToolDefinition(string Name, string Description)
{
    public string NormalizedName => Name.Trim();
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> All { get; }
    ToolDefinition Get(string name);
}

public sealed class InMemoryToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools;

    public InMemoryToolRegistry(IEnumerable<ToolDefinition> tools)
    {
        _tools = tools.ToDictionary(tool => tool.NormalizedName, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ToolDefinition> All => _tools.Values.ToArray();

    public ToolDefinition Get(string name)
    {
        if (_tools.TryGetValue(name.Trim(), out var tool))
        {
            return tool;
        }

        throw new KeyNotFoundException($"Tool '{name}' is not registered.");
    }
}
