using System.Text.Json;

namespace EciCas.Agents.Recall;

using EciCas.Core;

/// <summary>
/// JSONL-backed IAgentStateStore: append-only writes, full-scan lookup,
/// trimmed to a sliding window per path. A library, not a bus citizen.
///
/// The window is why this is not simply append-only. Every read asks for
/// maxPerPath: 1, so for a long time the file grew forever to hold lines
/// nothing could ever return — and the obvious fix, keeping only the newest
/// line per path, would have been worse than the leak. Impulse writes a
/// drive vector every time the persona is nudged, so those superseded lines
/// are the only record of how the persona has moved over time, and
/// Reflection now reads them as a trend. Collapsing to one would have
/// deleted the persona's history in the name of tidiness.
///
/// So: bounded, not truncated. Trimming happens on write, which costs a
/// rewrite of a file that is by construction never large.
/// </summary>
public sealed class JsonlAgentStateStore : IAgentStateStore
{
    /// <summary>
    /// States kept per path. Enough to show a direction across a long
    /// session, few enough that the file stays something you can read.
    /// </summary>
    public const int DefaultHistoryPerPath = 20;

    private readonly string _path;
    private readonly int _historyPerPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlAgentStateStore(string path, int historyPerPath = DefaultHistoryPerPath)
    {
        _path = path;
        _historyPerPath = Math.Max(1, historyPerPath);
    }

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

                // Skipped, not thrown on: TrimAsync deliberately keeps a
                // line it cannot parse, so the reader has to agree with it.
                // Otherwise one bad line takes down every read of the file —
                // Identity's persona and Reflection's drive history both
                // come through here.
                AgentStateRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<AgentStateRecord>(lines[i]);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (record is null || !pathSet.Contains(record.Path))
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

        var appended = records.Select(r => JsonSerializer.Serialize(r)).ToList();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllLinesAsync(_path, appended, cancellationToken).ConfigureAwait(false);
            await TrimAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops all but the newest <c>_historyPerPath</c> lines of each path,
    /// preserving file order so the reverse scan above still reads newest
    /// first. Called with the lock already held.
    ///
    /// A line that fails to parse is kept rather than dropped: this rewrites
    /// the whole file, and a trim is no place to decide that something
    /// hand-written or older than the current schema should stop existing.
    /// </summary>
    private async Task TrimAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keep = new bool[lines.Length];
        var trimmed = false;

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            string path;
            try
            {
                path = JsonSerializer.Deserialize<AgentStateRecord>(lines[i])!.Path;
            }
            catch (JsonException)
            {
                keep[i] = true;
                continue;
            }

            var count = seen.GetValueOrDefault(path);
            if (count >= _historyPerPath)
            {
                trimmed = true;
                continue;
            }

            seen[path] = count + 1;
            keep[i] = true;
        }

        if (!trimmed)
        {
            return;
        }

        // Through a temp file: a crash midway leaves the original intact,
        // where an in-place rewrite would leave the persona's state half
        // written and unparseable.
        var temp = _path + ".trim";
        await File.WriteAllLinesAsync(temp, lines.Where((_, i) => keep[i]), cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }
}
