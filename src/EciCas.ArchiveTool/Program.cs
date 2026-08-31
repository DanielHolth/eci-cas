using EciCas.Agents.Recall;
using EciCas.Core;

var directory = args.Length > 0 ? args[0] : "archive";
Directory.CreateDirectory(directory);

Console.WriteLine($"EciCas Archive Tool — {Path.GetFullPath(directory)}");
Console.WriteLine("Commands: list | show <category> [topic] [subtopic] | showall <category> [topic] [subtopic] | del <category> <index[,index...]> | del <category> <topic> [subtopic] | rebuild-index | help | exit");

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
                Console.WriteLine("list | show <category> [topic] [subtopic] | showall <category> [topic] [subtopic] | del <category> <index[,index...]> | del <category> <topic> [subtopic] | rebuild-index | help | exit");
                break;

            case "list":
                ListCategories(directory);
                break;

            case "show" when parts.Length >= 2:
                await ShowAsync(directory, parts[1], parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(3));
                break;

            case "showall" when parts.Length >= 2:
                await ShowAllAsync(directory, parts[1], parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(3));
                break;

            case "del" when parts.Length >= 3 && IsIndexList(parts[2]):
                await DeleteByIndexAsync(directory, parts[1], parts[2]);
                break;

            case "del" when parts.Length >= 3:
                await DeleteByFilterAsync(directory, parts[1], parts[2], parts.ElementAtOrDefault(3));
                break;

            case "rebuild-index":
                await ParquetArchiveStore.RebuildIndexAsync(directory, CancellationToken.None);
                Console.WriteLine("index.parquet rebuilt.");
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

static void ListCategories(string directory)
{
    var files = Directory.EnumerateFiles(directory, "*.parquet")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => !string.Equals(name, "index", StringComparison.OrdinalIgnoreCase))
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    foreach (var name in files)
    {
        Console.WriteLine(name);
    }
}

static async Task<List<ArchiveRecord>> FilteredRecordsAsync(string directory, string category, string? topic, string? subtopic)
{
    var path = ParquetArchiveStore.CategoryPathFor(directory, category);
    var records = await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None);

    if (topic is not null)
    {
        records = [.. records.Where(r => r.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase))];
    }
    if (subtopic is not null)
    {
        records = [.. records.Where(r => r.Subtopic.Contains(subtopic, StringComparison.OrdinalIgnoreCase))];
    }

    return records;
}

// Same format RecallAgent logs for its picked facts (RecallAgent.cs:80),
// minus Category since `show` is already scoped to one category.
static async Task ShowAsync(string directory, string category, string? topic, string? subtopic)
{
    var records = await FilteredRecordsAsync(directory, category, topic, subtopic);

    for (var i = 0; i < records.Count; i++)
    {
        var r = records[i];
        Console.WriteLine($"[{i}] {r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}");
    }

    Console.WriteLine($"{records.Count} record(s).");
}

static async Task ShowAllAsync(string directory, string category, string? topic, string? subtopic)
{
    var records = await FilteredRecordsAsync(directory, category, topic, subtopic);

    for (var i = 0; i < records.Count; i++)
    {
        var r = records[i];
        Console.WriteLine($"[{i}] category={r.Category} | topic={r.Topic} | subtopic={r.Subtopic} | subject={r.Subject} | key={r.Key} | value={r.Value} | importance={r.Importance:0.00} | domain={r.Domain} | timestamp={r.Timestamp:O}");
    }

    Console.WriteLine($"{records.Count} record(s).");
}

static bool IsIndexList(string token) =>
    token.Split(',', StringSplitOptions.RemoveEmptyEntries).All(t => int.TryParse(t, out _));

static async Task DeleteByIndexAsync(string directory, string category, string indexList)
{
    var path = ParquetArchiveStore.CategoryPathFor(directory, category);
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

    await ParquetArchiveStore.WriteRecordsAsync(path, records, CancellationToken.None);
    await ParquetArchiveStore.RebuildIndexAsync(directory, CancellationToken.None);
    Console.WriteLine($"Deleted. {records.Count} record(s) remain in {category}.");
}

static async Task DeleteByFilterAsync(string directory, string category, string topic, string? subtopic)
{
    var path = ParquetArchiveStore.CategoryPathFor(directory, category);
    var records = await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None);

    var toRemove = records
        .Where(r => r.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase))
        .Where(r => subtopic is null || r.Subtopic.Contains(subtopic, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (toRemove.Count == 0)
    {
        Console.WriteLine("No matching records.");
        return;
    }

    var kept = records.Except(toRemove).ToList();
    await ParquetArchiveStore.WriteRecordsAsync(path, kept, CancellationToken.None);
    await ParquetArchiveStore.RebuildIndexAsync(directory, CancellationToken.None);
    Console.WriteLine($"Deleted {toRemove.Count} record(s) matching topic~'{topic}'{(subtopic is null ? "" : $" subtopic~'{subtopic}'")}. {kept.Count} remain in {category}.");
}
