using System.Collections.Immutable;

namespace EciCas.Core;

/// <summary>
/// Immutable key-value bag on an Envelope. The "add functionality without
/// friction" lever: a new agent adds its own slot and its own typed accessor
/// extension (in its own file) without touching this type or any other agent.
/// </summary>
public sealed class MetaBag
{
    public static readonly MetaBag Empty = new(ImmutableDictionary<string, object?>.Empty);

    private readonly ImmutableDictionary<string, object?> _values;

    private MetaBag(ImmutableDictionary<string, object?> values) => _values = values;

    public MetaBag With(string key, object? value) => new(_values.SetItem(key, value));

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary>Overlays another bag's entries onto this one; keys in <paramref name="other"/> win.</summary>
    public MetaBag Merge(MetaBag other) => new(_values.SetItems(other._values));
}
