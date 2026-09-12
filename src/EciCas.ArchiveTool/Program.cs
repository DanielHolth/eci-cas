using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Agents.Utterances;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var directory = args.Length > 0 ? args[0] : "archive";

// Not CreateDirectory. A mistyped path used to be created on the spot and
// then reported as an archive holding nothing, which reads exactly like an
// empty archive and sends an operator hunting for the missing rows rather
// than the missing letter. An archive is something that already exists; the
// host makes it, this tool only edits it.
if (!Directory.Exists(directory))
{
    Console.WriteLine($"No archive at {Path.GetFullPath(directory)} — pass the host's archive directory, e.g. src/EciCas.Host/bin/Debug/net10.0/archive");
    return;
}

const string Usage = """
    list | show <category> [topic] [subtopic] | showall <category> [topic] [subtopic]
    recent | passages [count] | passage <id>
    utterances [count] | threads [count]
    thread merge <thread> <thread> | thread split <fact> | facts clear
    fact del <id...> | fact edit <id> <corrected sentence>
    del <category> <topic> <index[,index...]> | del <category> <topic> [subtopic]
    del recent <index[,index...]> | del passage <id>
    embed <model.onnx> <sentencepiece.bpe.model> | reset | help | exit
    """;

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

            case "recent":
                await ShowRecentAsync(directory);
                break;

            case "passages":
                await ShowPassagesAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "utterances":
                await ShowUtterancesAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "thread" when parts.Length == 4 && parts[1].Equals("merge", StringComparison.OrdinalIgnoreCase):
                await MergeThreadsAsync(directory, parts[2], parts[3]);
                break;

            case "thread" when parts.Length == 3 && parts[1].Equals("split", StringComparison.OrdinalIgnoreCase):
                await SplitThreadAsync(directory, parts[2]);
                break;

            case "fact" when parts.Length >= 3 && parts[1].Equals("del", StringComparison.OrdinalIgnoreCase):
                await DeleteFactsAsync(directory, parts[2..]);
                break;

            case "fact" when parts.Length >= 4 && parts[1].Equals("edit", StringComparison.OrdinalIgnoreCase):
                await EditFactAsync(directory, parts[2], string.Join(' ', parts[3..]));
                break;

            case "facts" when parts.Length == 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase):
                await ClearFactsAsync(directory);
                break;

            case "threads":
                await ShowThreadsAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "passage" when parts.Length >= 2:
                await ShowPassageAsync(directory, parts[1]);
                break;

            case "del" when parts.Length >= 3 && IsLane(parts[1]) && IsIndexList(parts[2]):
                await DeleteRecentAsync(directory, parts[2]);
                break;

            case "del" when parts.Length >= 3 && parts[1].Equals("passage", StringComparison.OrdinalIgnoreCase):
                await DeletePassageAsync(directory, parts[2]);
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
                Console.WriteLine("embed <model.onnx> <sentencepiece.bpe.model> - use the same paths the host is configured with, or the vectors will not count.");
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

// Backfill, for an archive no host is booting: the same job runs at startup
// against the embedder the host resolved, so a running deployment never needs
// this. What it is still for is the other archive -- a copy under test, one
// restored from a backup, one whose weights you want to change without
// starting the thing.
//
// Which is why the model path is asked for rather than guessed, and why it
// matters exactly: the vector is stamped with "onnx:<path>" and the reader
// compares that string to what the host has configured. Backfilling with a
// copy of the same weights under a different path produces vectors that are
// correct and ignored.
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

    Console.WriteLine($"Embedding with {provider.ModelId}");

    var (rows, files) = await ArchiveBackfill.RunAsync(
        directory,
        provider,
        onFile: (file, count) => Console.WriteLine($"  {Path.GetFileName(file)}: {count} row(s)"),
        CancellationToken.None);

    Console.WriteLine($"Embedded {rows} row(s) across {files} file(s); everything else was already current.");
}

// The directory listing IS the index — there is no index file to consult or
// rebuild, so this can't disagree with what the store would report.
static void ListPairs(string directory)
{
    foreach (var pair in ParquetArchiveStore.PairsIn(directory)
        .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.Topic, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{pair.Category}/{pair.Topic}");
    }
}

/// <summary>
/// Rows across every pair matching the given category (and topic, if given).
/// `show` may span several files; `del` never does, so it resolves to one
/// pair and reads that file directly.
/// </summary>
static async Task<List<ArchiveRecord>> FilteredRecordsAsync(string directory, string category, string? topic, string? subtopic)
{
    var records = new List<ArchiveRecord>();
    var pairs = ParquetArchiveStore.PairsIn(directory)
        .Where(p => p.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
        .Where(p => topic is null || p.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Topic, StringComparer.OrdinalIgnoreCase);

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

/// <summary>The one file a delete is allowed to touch.</summary>
static string? ResolvePairPath(string directory, string category, string topic)
{
    var path = ParquetArchiveStore.PairPathFor(directory, new ArchivePair(category, topic));
    if (File.Exists(path))
    {
        return path;
    }

    Console.WriteLine($"No pair {category}/{topic}. Try 'list'.");
    return null;
}

// Index-based delete reads one pair file, so the indices it takes are the
// ones `show <category> <topic>` printed for that same single pair.
static async Task DeleteByIndexAsync(string directory, string category, string topic, string indexList)
{
    var path = ResolvePairPath(directory, category, topic);
    if (path is null)
    {
        return;
    }

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
    var path = ResolvePairPath(directory, category, topic);
    if (path is null)
    {
        return;
    }

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
// Wipes every pair file and reseeds the
// single fact "reset parquet" always leaves behind, so a fresh archive is
// never truly empty: assistant/system/eci/this/version = 0.1.
static async Task ResetAsync(string directory)
{
    foreach (var file in Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories))
    {
        File.Delete(file);
    }

    // The utterance log's turn counter is not a parquet file, and it is the
    // denominator of every hit rate in it. Left behind, a reset archive
    // reports a thousand turns and no rows, which is a rate of zero over a
    // corpus that never existed.
    var turns = Path.Combine(directory, ParquetUtteranceLog.DirectoryName, ParquetUtteranceLog.TurnCountFileName);
    if (File.Exists(turns))
    {
        File.Delete(turns);
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

// The recent lane. Not a pair and so not in `list`: recent.parquet has no
// category~topic name to decode, and the store keeps it as one flat drawer
// per tier beside the index rather than inside it. It still holds rows a
// person might want gone -- a fact the persona picked up wrong is in here as
// well as in its pair -- so it gets a view and a delete of its own.
static async Task ShowRecentAsync(string directory)
{
    var path = Path.Combine(directory, ParquetArchiveStore.RecentFileName);
    if (!File.Exists(path))
    {
        Console.WriteLine("No recent lane there.");
        return;
    }

    var records = await LaneAsync(path);
    Console.WriteLine($"recent - {records.Count} record(s)");
    for (var i = 0; i < records.Count; i++)
    {
        var r = records[i];
        Console.WriteLine($"  [{i}] {r.Timestamp:yyyy-MM-dd HH:mm} {r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key} = {r.Value}");
    }
}

// Newest first, which is both how the store reads the lane and the order
// anybody scanning it wants. The index a delete takes is the one printed
// here, so the two share one function rather than two sorts that could drift.
static async Task<List<ArchiveRecord>> LaneAsync(string path) =>
    [.. (await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None)).OrderByDescending(r => r.Timestamp)];

static bool IsLane(string token) =>
    token.Equals("recent", StringComparison.OrdinalIgnoreCase);

static async Task DeleteRecentAsync(string directory, string indexList)
{
    var path = Path.Combine(directory, ParquetArchiveStore.RecentFileName);
    if (!File.Exists(path))
    {
        Console.WriteLine("No recent lane there.");
        return;
    }

    var records = await LaneAsync(path);
    foreach (var i in indexList.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).OrderByDescending(i => i))
    {
        if (i < 0 || i >= records.Count)
        {
            Console.WriteLine($"Index {i} out of range, skipped.");
            continue;
        }
        records.RemoveAt(i);
    }

    await SaveAsync(path, records);
    Console.WriteLine($"Deleted. {records.Count} record(s) remain in recent.");
}

// Reflection's corpus, which no other command touches: it is prose the
// persona wrote to itself, one note per event-series, not facts in drawers.
// Shared-tier only by construction -- a self-critique belongs to the persona
// rather than to whoever happened to be talking -- so no profile argument
// here and no union read.
static ParquetPassageStore Passages(string directory) => new(directory);

static async Task ShowPassagesAsync(string directory, string? countArg)
{
    var all = await Passages(directory).AllAsync(CancellationToken.None);
    if (all.Count == 0)
    {
        Console.WriteLine($"No {ParquetPassageStore.FileName} yet - Reflection has not written a note in this archive.");
        return;
    }

    var count = int.TryParse(countArg, out var n) ? n : 20;
    foreach (var p in all.OrderByDescending(p => p.Timestamp).Take(count))
    {
        var pairs = p.Pairs.Count == 0 ? "-" : string.Join(" ", p.Pairs.Select(x => $"{x.Category}/{x.Topic}"));
        Console.WriteLine($"{p.Id}  {p.Timestamp:yyyy-MM-dd HH:mm}  gen={p.Generation} echo={p.EchoDepth}  {pairs}");
        Console.WriteLine($"  {Oneline(p.Text, 160)}");
    }

    Console.WriteLine($"{all.Count} passage(s); showing up to {count}. `passage <id>` for one in full.");
}

// A note runs to paragraphs, so the listing shows an opening and this shows
// the thing itself. The id prefix is enough to name one, the way a commit is
// named by its first characters.
static async Task ShowPassageAsync(string directory, string id)
{
    var passage = await FindPassageAsync(directory, id);
    if (passage is null)
    {
        return;
    }

    Console.WriteLine($"id        {passage.Id}");
    Console.WriteLine($"written   {passage.Timestamp:O}");
    Console.WriteLine($"lineage   generation {passage.Generation}, echo depth {passage.EchoDepth}{(passage.ParentIds.Count == 0 ? "" : $", after {string.Join(", ", passage.ParentIds)}")}");
    Console.WriteLine($"pairs     {(passage.Pairs.Count == 0 ? "-" : string.Join(", ", passage.Pairs.Select(x => $"{x.Category}/{x.Topic}")))}");
    Console.WriteLine($"vector    {(passage.Embedding.Length == 0 ? "none" : $"{passage.Embedding.Length}d {passage.ModelId}")}");
    Console.WriteLine();
    Console.WriteLine(passage.Text);
}

static async Task DeletePassageAsync(string directory, string id)
{
    var passage = await FindPassageAsync(directory, id);
    if (passage is null)
    {
        return;
    }

    // The store's own replace path: one write, one temp file, one move. A
    // delete here is a revisit that adds nothing.
    await Passages(directory).WriteAsync([], passage.Id, CancellationToken.None);
    Console.WriteLine($"Deleted passage {passage.Id}.");
}

static async Task<Passage?> FindPassageAsync(string directory, string id)
{
    var all = await Passages(directory).AllAsync(CancellationToken.None);
    var matches = all.Where(p => p.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();

    switch (matches.Count)
    {
        case 0:
            Console.WriteLine($"No passage {id}. Try 'passages'.");
            return null;

        case 1:
            return matches[0];

        default:
            Console.WriteLine($"{id} names {matches.Count} passages: {string.Join(", ", matches.Select(m => m.Id))}. Say more of it.");
            return null;
    }
}

static string Oneline(string text, int width)
{
    var flat = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    return flat.Length <= width ? flat : flat[..width] + "...";
}

/// <summary>
/// The inverted archive, read raw. Nothing here interprets: an operator
/// turning the flag on for the first time needs to see that rows landed, that
/// they carry a vector, and which of them the reader will treat as retired --
/// which are exactly the three columns a wrong answer would be explained by.
/// </summary>
static async Task ShowUtterancesAsync(string directory, string? count)
{
    var log = new ParquetUtteranceLog(directory);
    var said = await log.AllAsync(CancellationToken.None);
    var rows = await new ParquetFactLog(directory).AllAsync(CancellationToken.None);
    if (rows.Count == 0)
    {
        Console.WriteLine($"{said.Count} utterance(s) on file and no facts read out of them yet.");
        return;
    }

    var take = int.TryParse(count, out var n) && n > 0 ? n : 20;
    Console.WriteLine($"{rows.Count} fact(s) from {said.Count} utterance(s) over {log.TurnsRecorded} turn(s); newest {Math.Min(take, rows.Count)}:");

    foreach (var r in rows.OrderByDescending(r => r.Timestamp).Take(take))
    {
        var marks = string.Concat(
            r.Embedding is { Length: > 0 } ? "v" : "-",
            r.ThreadId is null ? "-" : "t",
            r.SupersededBy is null ? "-" : "x");
        Console.WriteLine($"  {r.Id[..8]}  {r.Timestamp:yyyy-MM-dd HH:mm} [{marks}] {r.HitCount,3} hits  {Oneline(r.Text, 68)}");
    }

    Console.WriteLine("  flags: v=vector t=threaded x=superseded; the first column is the id `fact del`/`fact edit` take.");
}

/// <summary>
/// Take one wrong row out, by id prefix — the surgical form of `facts clear`,
/// for the case that does not want the whole index rebuilt: an extractor that
/// minted a sentence nobody said. The utterance it was read from is untouched,
/// so if the sentence really did say it, the next backfill mints it again;
/// that is the signal that the extractor and not the row is the thing to fix.
/// </summary>
static async Task DeleteFactsAsync(string directory, string[] prefixes)
{
    var log = new ParquetFactLog(directory);
    var rows = await log.AllAsync(CancellationToken.None);

    var wanted = new List<Fact>();
    foreach (var prefix in prefixes)
    {
        if (ResolveFact(rows, prefix) is { } row)
        {
            wanted.Add(row);
        }
    }

    if (wanted.Count == 0)
    {
        return;
    }

    var gone = await log.RemoveAsync([.. wanted.Select(r => r.Id)], CancellationToken.None);
    foreach (var row in wanted)
    {
        Console.WriteLine($"  removed {Oneline(row.Text, 70)}");
    }

    Console.WriteLine($"Deleted {gone} row(s). The utterances they came from are untouched.");
}

/// <summary>
/// Correct what a row says, keeping its place in its thread. The vector goes
/// with the text — a row still pointing at the old sentence answers the old
/// question — so the corrected row is bare until the next `embed` or boot.
/// </summary>
static async Task EditFactAsync(string directory, string prefix, string text)
{
    var log = new ParquetFactLog(directory);
    var rows = await log.AllAsync(CancellationToken.None);
    if (ResolveFact(rows, prefix) is not { } row)
    {
        return;
    }

    Console.WriteLine($"  was  {Oneline(row.Text, 70)}");
    await log.ReviseAsync(row.Id, text, CancellationToken.None);
    Console.WriteLine($"  now  {Oneline(text, 70)}");
    Console.WriteLine("Re-embed to give it a vector again: `embed <model.onnx> <sentencepiece.bpe.model>`, or just boot the host.");
}

/// <summary>One fact by id prefix, or null when the prefix names none or many.</summary>
static Fact? ResolveFact(IReadOnlyList<Fact> rows, string prefix)
{
    var matches = rows.Where(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
    switch (matches.Count)
    {
        case 0:
            Console.WriteLine($"No fact {prefix}. Try 'utterances'.");
            return null;

        case 1:
            return matches[0];

        default:
            Console.WriteLine($"{prefix} names more than one fact. Say more of it.");
            return null;
    }
}

/// <summary>
/// Throw the index away. Not destructive in the sense the word usually has
/// here: facts, vectors and threads are all computed from the utterance log,
/// which this does not open, so the next boot's backfill reads every line
/// again and files it under whatever extractor is configured now. It is the
/// operator's half of "the index is disposable" -- how a better extractor or
/// a swapped embedder reaches a warm archive.
/// </summary>
static async Task ClearFactsAsync(string directory)
{
    var facts = new ParquetFactLog(directory);
    var before = (await facts.AllAsync(CancellationToken.None)).Count;
    await facts.ClearAsync(CancellationToken.None);
    Console.WriteLine($"Dropped {before} fact(s). The next boot reads them out of the utterance log again.");
}

/// <summary>
/// Threads, largest first. This is the one view that says whether threading
/// is working: a corpus that is all singletons is one that never merged, and
/// a thread holding a dozen unrelated sentences is the failure a consolidator
/// exists to prevent.
/// </summary>
static async Task ShowThreadsAsync(string directory, string? count)
{
    var rows = await new ParquetFactLog(directory).AllAsync(CancellationToken.None);
    var threads = rows
        .Where(r => r.ThreadId is not null)
        .GroupBy(r => r.ThreadId!)
        .OrderByDescending(g => g.Count())
        .ThenByDescending(g => g.Max(r => r.Timestamp))
        .ToList();

    if (threads.Count == 0)
    {
        Console.WriteLine("No threads. Rows are threaded at write time, or by the backfill at boot.");
        return;
    }

    var singletons = threads.Count(t => t.Count() == 1);
    Console.WriteLine($"{threads.Count} thread(s) over {rows.Count} row(s); {singletons} hold one row.");

    var take = int.TryParse(count, out var n) && n > 0 ? n : 15;
    foreach (var thread in threads.Take(take))
    {
        var newest = thread.OrderByDescending(r => r.Timestamp).First();
        Console.WriteLine($"  {thread.Key[..8]}  {thread.Count(),3} row(s)  {Oneline(newest.Text, 72)}");
    }
}

/// <summary>
/// Move every row of one thread onto another. A thread is a derived column,
/// so this is a correction and not a rewrite: no text changes, nothing is
/// deleted, and the next backfill would reach the same place if it could
/// judge. The surviving id is the first argument, because a merge has a
/// direction and the operator should be the one choosing it.
/// </summary>
static async Task MergeThreadsAsync(string directory, string keep, string absorb)
{
    var log = new ParquetFactLog(directory);
    var rows = await log.AllAsync(CancellationToken.None);

    var into = Resolve(rows, keep);
    var from = Resolve(rows, absorb);
    if (into is null || from is null || into == from)
    {
        Console.WriteLine(into == from && into is not null ? "Those are the same thread." : "Say more of the thread id -- 'threads' lists them.");
        return;
    }

    var moving = rows.Where(r => r.ThreadId == from).ToList();
    await log.UpdateDerivedAsync([.. moving.Select(r => new FactDerived(r.Id, ThreadId: into))], CancellationToken.None);
    Console.WriteLine($"Moved {moving.Count} row(s) from {from[..8]} into {into[..8]}.");
}

/// <summary>
/// Give one row a thread of its own. The recoverable half of a threading
/// mistake: a false merge hides a fact behind another one at read time, and
/// this is how it is undone without touching what was said.
/// </summary>
static async Task SplitThreadAsync(string directory, string utterance)
{
    var log = new ParquetFactLog(directory);
    var rows = await log.AllAsync(CancellationToken.None);
    var matches = rows.Where(r => r.Id.StartsWith(utterance, StringComparison.OrdinalIgnoreCase)).ToList();

    if (matches.Count != 1)
    {
        Console.WriteLine(matches.Count == 0 ? $"No utterance {utterance}." : $"{utterance} names {matches.Count} utterances. Say more of it.");
        return;
    }

    var row = matches[0];
    // And it is no longer retired: the row that superseded it belonged to
    // the thread this row just left, so the link would leave a fact true and
    // unreadable. The empty string is how a supersession is unset.
    await log.UpdateDerivedAsync([new FactDerived(row.Id, ThreadId: row.Id, SupersededBy: "")], CancellationToken.None);
    Console.WriteLine($"{Oneline(row.Text, 60)} is now its own thread.");
}

/// <summary>A thread id by prefix, or null when the prefix names none or many.</summary>
static string? Resolve(IReadOnlyList<Fact> rows, string prefix)
{
    var ids = rows.Where(r => r.ThreadId is not null)
        .Select(r => r.ThreadId!)
        .Distinct(StringComparer.Ordinal)
        .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Take(2)
        .ToList();

    return ids.Count == 1 ? ids[0] : null;
}

