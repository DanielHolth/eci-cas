using EciCas.Agents.Recall;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var directory = args.Length > 0 ? args[0] : "archive";
Directory.CreateDirectory(directory);

const string Usage = "list | show <category> [topic] [subtopic] | showall <category> [topic] [subtopic] | del <category> <topic> <index[,index...]> | del <category> <topic> [subtopic] | embed <model.onnx> <vocab.txt> | reset | help | exit";

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

            case "embed" when parts.Length >= 3:
                await EmbedAsync(directory, parts[1], parts[2]);
                break;

            case "embed":
                Console.WriteLine("embed <model.onnx> <vocab.txt> - use the same paths the host is configured with, or the vectors will not count.");
                break;

            case "reset":
                await ResetAsync(directory);
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

// Backfill. Rows written before the embedder existed, or while it was down,
// make their whole pair fall back to the pre-vector read path - a pair is
// only swept by cosine when every row in it carries a current vector, so one
// bare row costs the file its ranking. This is what turns an existing
// archive on.
//
// The model path is asked for rather than guessed, and it matters exactly:
// the vector is stamped with "onnx:<path>" and the reader compares that
// string to what the host has configured. Backfilling with a copy of the same
// weights under a different path produces vectors that are correct and
// ignored.
static async Task EmbedAsync(string directory, string modelPath, string vocabPath)
{
    using var provider = new OnnxEmbeddingProvider(
        Options.Create(new EmbeddingOptions { Provider = "onnx", ModelPath = modelPath, VocabPath = vocabPath }),
        NullLogger<OnnxEmbeddingProvider>.Instance);

    if (!provider.Available)
    {
        Console.WriteLine($"No embedder: check that {modelPath} and {vocabPath} both exist.");
        return;
    }

    var modelId = provider.ModelId;
    Console.WriteLine($"Embedding with {modelId}");

    // Every directory, because a profile tier is a directory of pair files
    // like any other and its rows need vectors just as much.
    var files = Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories);
    var embedded = 0;
    var touched = 0;

    foreach (var file in files)
    {
        var rows = await ParquetArchiveStore.ReadRecordsAsync(file, CancellationToken.None);
        var pending = rows
            .Select((r, i) => (Record: r, Index: i))
            .Where(x => !x.Record.HasVector(modelId))
            .ToList();

        if (pending.Count == 0)
        {
            continue;
        }

        var vectors = await provider.EmbedAsync(
            [.. pending.Select(x => x.Record.EmbeddedText)], EmbeddingKind.Passage, CancellationToken.None);

        if (vectors.Count != pending.Count)
        {
            Console.WriteLine($"  {Path.GetFileName(file)}: embedder returned {vectors.Count} vector(s) for {pending.Count} row(s), skipped");
            continue;
        }

        var updated = rows.ToList();
        for (var i = 0; i < pending.Count; i++)
        {
            var text = pending[i].Record.EmbeddedText;
            updated[pending[i].Index] = pending[i].Record with
            {
                Embedding = vectors[i],
                EmbeddingModelId = modelId,
                EmbeddingHash = ArchiveEmbedding.HashOf(text),
            };
        }

        await ParquetArchiveStore.WriteRecordsAsync(file, updated, CancellationToken.None);
        Console.WriteLine($"  {Path.GetFileName(file)}: {pending.Count} row(s)");
        embedded += pending.Count;
        touched++;
    }

    Console.WriteLine($"Embedded {embedded} row(s) across {touched} file(s); {files.Length - touched} file(s) were already current.");
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
// would keep offering Librarian a topic with nothing under it.
// Wipes every pair file — shared tier and all profiles' own — and reseeds the
// single fact "reset parquet" always leaves behind, so a fresh archive is
// never truly empty: assistant/system/eci/this/version = 0.1.
static async Task ResetAsync(string directory)
{
    foreach (var file in Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories))
    {
        File.Delete(file);
    }

    var record = new ArchiveRecord(
        AssistantScope.Name, AssistantScope.System, "eci", "this", "version", "0.1",
        DateTimeOffset.UtcNow, ArchiveDomain.Internal, 1.0);

    var path = ParquetArchiveStore.PairPathFor(directory, new ArchivePair(AssistantScope.Name, AssistantScope.System));
    await ParquetArchiveStore.WriteRecordsAsync(path, [record], CancellationToken.None);
    Console.WriteLine("Parquet reset.");
}

static async Task SaveAsync(string path, List<ArchiveRecord> records)
{
    if (records.Count == 0)
    {
        File.Delete(path);
        return;
    }

    await ParquetArchiveStore.WriteRecordsAsync(path, records, CancellationToken.None);
}
