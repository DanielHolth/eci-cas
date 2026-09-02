using System.Text.Json;

namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// JSONL-backed IAgentStateStore: append-only writes, full-scan lookup. A
/// library, not a bus citizen.
/// </summary>
public sealed class JsonlAgentStateStore : IAgentStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlAgentStateStore(string path) => _path = path;

    public async Task<IReadOnlyList<AgentStateRecord>> LookupAsync(IReadOnlyList<string> paths, int maxPerPath, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<AgentStateRecord>();
        var perPathCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            // Newest first: a plain reverse scan, since the file is append-only.
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<AgentStateRecord>(lines[i])!;
                if (!pathSet.Contains(record.Path))
                {
                    continue;
                }

                var count = perPathCount.GetValueOrDefault(record.Path);
                if (count >= maxPerPath)
                {
                    continue;
                }

                perPathCount[record.Path] = count + 1;
                results.Add(record);
            }
        }
        finally
        {
            _lock.Release();
        }

        return results;
    }

    public async Task WriteAsync(IReadOnlyList<AgentStateRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        var lines = records.Select(r => JsonSerializer.Serialize(r));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllLinesAsync(_path, lines, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
