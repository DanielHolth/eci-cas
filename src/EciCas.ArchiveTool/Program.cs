using EciCas.Agents.Recall;
using EciCas.Core;

var directory = args.Length > 0 ? args[0] : "archive";
Directory.CreateDirectory(directory);

const string Usage = "list | show <category> [topic] [subtopic] | showall <category> [topic] [subtopic] | del <category> <topic> <index[,index...]> | del <category> <topic> [subtopic] | help | exit";

Console.WriteLine($"EciCas Archive Tool — {Path.GetFullPath(directory)}");
Console.WriteLine($"Commands: {Usage}");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null)
    {
        break;
    }

    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
        continue;
    }

    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "exit":
            case "quit":
                return;

            case "help":
                Console.WriteLine(Usage);
                break;

            case "list":
                ListPairs(directory);
                break;

            case "show" when parts.Length >= 2:
                await ShowAsync(directory, parts[1], parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(3), full: false);
                break;

            case "showall" when parts.Length >= 2:
                await ShowAsync(directory, parts[1], parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(3), full: true);
                break;

            case "del" when parts.Length >= 4 && IsIndexList(parts[3]):
                await DeleteByIndexAsync(directory, parts[1], parts[2], parts[3]);
                break;

            case "del" when parts.Length >= 3:
                await DeleteByFilterAsync(directory, parts[1], parts[2], parts.ElementAtOrDefault(3));
                break;

            default:
                Console.WriteLine("Unrecognized command. Type 'help'.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

// The directory listing IS the index — there is no index file to consult or
// rebuild, so this can't disagree with what the store would report.
static void ListPairs(string directory)
{
    foreach (var pair in ParquetArchiveStore.PairsIn(directory).OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Topic, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{pair.Category}/{pair.Topic}");
    }
}

/// <summary>
/// Rows across every pair matching the given category (and topic, if given).
/// `show` may span several files; `del` never does, so it takes the pair
/// explicitly and reads one file directly.
/// </summary>
static async Task<List<ArchiveRecord>> FilteredRecordsAsync(string directory, string category, string? topic, string? subtopic)
{
    var pairs = ParquetArchiveStore.PairsIn(directory)
        .Where(p => p.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
        .Where(p => topic is null || p.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Topic, StringComparer.OrdinalIgnoreCase);

    var records = new List<ArchiveRecord>();
    foreach (var pair in pairs)
    {
        records.AddRange(await ParquetArchiveStore.ReadRecordsAsync(ParquetArchiveStore.PairPathFor(directory, pair), CancellationToken.None));
    }

    return subtopic is null
        ? records
        : [.. records.Where(r => r.Subtopic.Contains(subtopic, StringComparison.OrdinalIgnoreCase))];
}

static async Task ShowAsync(string directory, string category, string? topic, string? subtopic, bool full)
{
    var records = await FilteredRecordsAsync(directory, category, topic, subtopic);

    for (var i = 0; i < records.Count; i++)
    {
        var r = records[i];
        Console.WriteLine(full
            ? $"[{i}] category={r.Category} | topic={r.Topic} | subtopic={r.Subtopic} | subject={r.Subject} | key={r.Key} | value={r.Value} | importance={r.Importance:0.00} | domain={r.Domain} | timestamp={r.Timestamp:O}"
            : $"[{i}] {r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}");
    }

    Console.WriteLine($"{records.Count} record(s).");
}

static bool IsIndexList(string token) =>
    token.Split(',', StringSplitOptions.RemoveEmptyEntries).All(t => int.TryParse(t, out _));

// Index-based delete reads one pair file, so the indices it takes are the
// ones `show <category> <topic>` printed for that same single pair.
static async Task DeleteByIndexAsync(string directory, string category, string topic, string indexList)
{
    var path = ParquetArchiveStore.PairPathFor(directory, new ArchivePair(category, topic));
    var records = await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None);

    var indices = indexList.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse)
        .OrderByDescending(i => i)
        .ToList();

    foreach (var i in indices)
    {
        if (i < 0 || i >= records.Count)
        {
            Console.WriteLine($"Index {i} out of range, skipped.");
            continue;
        }
        records.RemoveAt(i);
    }

    await SaveAsync(path, records);
    Console.WriteLine($"Deleted. {records.Count} record(s) remain in {category}/{topic}.");
}

static async Task DeleteByFilterAsync(string directory, string category, string topic, string? subtopic)
{
    var path = ParquetArchiveStore.PairPathFor(directory, new ArchivePair(category, topic));
    var records = await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None);

    var toRemove = records
        .Where(r => subtopic is null || r.Subtopic.Contains(subtopic, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (toRemove.Count == 0)
    {
        Console.WriteLine("No matching records.");
        return;
    }

    var kept = records.Except(toRemove).ToList();
    await SaveAsync(path, kept);
    Console.WriteLine($"Deleted {toRemove.Count} record(s) from {category}/{topic}{(subtopic is null ? "" : $" subtopic~'{subtopic}'")}. {kept.Count} remain.");
}

// An emptied pair loses its file rather than keeping a zero-row one: the
// file's existence is what puts the pair in the index, so leaving it behind
// would keep offering Reasoning a topic with nothing under it.
static async Task SaveAsync(string path, List<ArchiveRecord> records)
{
    if (records.Count == 0)
    {
        File.Delete(path);
        return;
    }

    await ParquetArchiveStore.WriteRecordsAsync(path, records, CancellationToken.None);
}
