using EciCas.Agents.Passages;
using EciCas.Agents.Recall;
using EciCas.Agents.Utterances;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// An operator's window onto one archive directory, in the shape the archive
// actually has now: an append-only log of what was said, a disposable index
// of facts read out of it, the passages Reflection and Hindsight write, and
// what is left of the old pair files.
//
// The commands are grouped that way on purpose. Everything under `fact` is
// recomputable and therefore safe to break; `said` is ground truth and has no
// delete at all, because nothing can put a sentence back.

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
    facts [count] | fact <id> | fact del <id...> | fact edit <id> <corrected sentence>
    threads [count] | thread merge <thread> <thread> | thread split <id> | facts clear
    said [count] | replies [count]
    passages [count] | passage <id> | passage del <id>
    internal | internal del <category> <topic> <index[,index...]>
    embed <model.onnx> <sentencepiece.bpe.model> | erase | help | exit
    """;

Console.WriteLine($"EciCas Archive Tool — {Path.GetFullPath(directory)}");
Console.WriteLine($"Commands: {Usage}");
await SummariseAsync(directory);

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

            case "facts" when parts.Length == 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase):
                await ClearFactsAsync(directory);
                break;

            case "facts":
                await ShowFactsAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "fact" when parts.Length >= 3 && parts[1].Equals("del", StringComparison.OrdinalIgnoreCase):
                await DeleteFactsAsync(directory, parts[2..]);
                break;

            case "fact" when parts.Length >= 4 && parts[1].Equals("edit", StringComparison.OrdinalIgnoreCase):
                await EditFactAsync(directory, parts[2], string.Join(' ', parts[3..]));
                break;

            case "fact" when parts.Length == 2:
                await ShowFactAsync(directory, parts[1]);
                break;

            case "threads":
                await ShowThreadsAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "thread" when parts.Length == 4 && parts[1].Equals("merge", StringComparison.OrdinalIgnoreCase):
                await MergeThreadsAsync(directory, parts[2], parts[3]);
                break;

            case "thread" when parts.Length == 3 && parts[1].Equals("split", StringComparison.OrdinalIgnoreCase):
                await SplitThreadAsync(directory, parts[2]);
                break;

            case "said":
                await ShowSaidAsync(directory, parts.ElementAtOrDefault(1), replies: false);
                break;

            case "replies":
                await ShowSaidAsync(directory, parts.ElementAtOrDefault(1), replies: true);
                break;

            case "passages":
                await ShowPassagesAsync(directory, parts.ElementAtOrDefault(1));
                break;

            case "passage" when parts.Length >= 3 && parts[1].Equals("del", StringComparison.OrdinalIgnoreCase):
                await DeletePassageAsync(directory, parts[2]);
                break;

            case "passage" when parts.Length >= 2:
                await ShowPassageAsync(directory, parts[1]);
                break;

            case "internal" when parts.Length >= 5 && parts[1].Equals("del", StringComparison.OrdinalIgnoreCase) && IsIndexList(parts[4]):
                await DeleteInternalAsync(directory, parts[2], parts[3], parts[4]);
                break;

            case "internal":
                await ShowInternalAsync(directory);
                break;

            case "embed" when parts.Length >= 3:
                await EmbedAsync(directory, parts[1], parts[2]);
                break;

            case "embed":
                Console.WriteLine("embed <model.onnx> <sentencepiece.bpe.model> — use the same paths the host is configured with, or the vectors will not count.");
                break;

            case "erase":
                await EraseAsync(directory);
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

/// <summary>
/// What is in here, in one line, before the first command. An archive whose
/// facts are empty and whose utterance log is not is mid-backfill rather than
/// broken, and that is worth saying before an operator starts deleting things
/// to find out.
/// </summary>
static async Task SummariseAsync(string directory)
{
    var said = new ParquetUtteranceLog(directory);
    var utterances = await said.AllAsync(CancellationToken.None);
    var replies = await said.RepliesAsync(CancellationToken.None);
    var facts = await Facts(directory).AllAsync(CancellationToken.None);
    var passages = await Passages(directory).AllAsync(CancellationToken.None);
    var internals = (await InternalRowsAsync(directory)).Count;

    Console.WriteLine(
        $"{utterances.Count} utterance(s) + {replies.Count} repl(ies) over {said.TurnsRecorded} turn(s); " +
        $"{facts.Count} fact(s), {passages.Count} passage(s), {internals} internal row(s).");
}

// ---------------------------------------------------------------- facts ----
//
// The index. Every row here was read out of an utterance and can be read out
// of it again, which is what makes deleting and rewriting them a normal thing
// to do rather than a loss.

static ParquetFactLog Facts(string directory) => new(directory);

static async Task ShowFactsAsync(string directory, string? count)
{
    var rows = await Facts(directory).AllAsync(CancellationToken.None);
    if (rows.Count == 0)
    {
        Console.WriteLine("No facts. The next boot reads them out of the utterance log.");
        return;
    }

    var take = int.TryParse(count, out var n) && n > 0 ? n : 20;
    Console.WriteLine($"{rows.Count} fact(s); newest {Math.Min(take, rows.Count)}:");

    foreach (var r in rows.OrderByDescending(r => r.Timestamp).Take(take))
    {
        var marks = string.Concat(
            r.Embedding is { Length: > 0 } ? "v" : "-",
            r.ThreadId is null ? "-" : "t",
            r.SupersededBy is null ? "-" : "x");
        Console.WriteLine($"  {r.Id[..8]}  {r.Timestamp:yyyy-MM-dd HH:mm} [{marks}] {r.HitCount,3} hits  {Oneline(r.Text, 68)}");
    }

    Console.WriteLine("  flags: v=vector t=threaded x=superseded; the first column is the id every `fact` command takes.");
}

/// <summary>
/// One row with its derived columns spelled out, and the sentence it was read
/// out of. Which is the question an operator actually has about a fact that
/// looks wrong: whether the extractor misread something real or minted a
/// claim out of nothing.
/// </summary>
static async Task ShowFactAsync(string directory, string prefix)
{
    var rows = await Facts(directory).AllAsync(CancellationToken.None);
    if (ResolveFact(rows, prefix) is not { } row)
    {
        return;
    }

    Console.WriteLine($"id         {row.Id}");
    Console.WriteLine($"said       {row.Timestamp:O} by {row.Speaker}");
    Console.WriteLine($"thread     {row.ThreadId ?? "-"}{(row.SupersededBy is null ? "" : $", superseded by {row.SupersededBy}")}");
    Console.WriteLine($"vector     {(row.Embedding is { Length: > 0 } ? $"{row.Embedding.Length}d {row.EmbeddingModelId}" : "none")}");
    Console.WriteLine($"keywords   {(row.Keywords.Count == 0 ? "-" : string.Join(", ", row.Keywords))}");
    Console.WriteLine($"used       {row.HitCount} time(s)");
    Console.WriteLine();
    Console.WriteLine(row.Text);

    var source = (await new ParquetUtteranceLog(directory).AllAsync(CancellationToken.None))
        .FirstOrDefault(u => u.Id == row.SourceId);
    Console.WriteLine();
    Console.WriteLine(source is null
        ? $"out of utterance {row.SourceId}, which is not in the log — so this row cannot be rebuilt."
        : $"out of: {Oneline(source.Text, 200)}");
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
    var log = Facts(directory);
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
    var log = Facts(directory);
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

/// <summary>
/// Throw the index away. Not destructive in the sense the word usually has
/// here: facts, vectors and threads are all computed from the utterance log,
/// which this does not open, so the next boot's backfill reads every line
/// again and files it under whatever extractor is configured now. It is the
/// operator's half of "the index is disposable" — how a better extractor or
/// a swapped embedder reaches a warm archive.
/// </summary>
static async Task ClearFactsAsync(string directory)
{
    var facts = Facts(directory);
    var before = (await facts.AllAsync(CancellationToken.None)).Count;
    await facts.ClearAsync(CancellationToken.None);
    Console.WriteLine($"Dropped {before} fact(s). The next boot reads them out of the utterance log again.");
}

/// <summary>One fact by id prefix, or null when the prefix names none or many.</summary>
static Fact? ResolveFact(IReadOnlyList<Fact> rows, string prefix)
{
    var matches = rows.Where(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
    switch (matches.Count)
    {
        case 0:
            Console.WriteLine($"No fact {prefix}. Try 'facts'.");
            return null;

        case 1:
            return matches[0];

        default:
            Console.WriteLine($"{prefix} names more than one fact. Say more of it.");
            return null;
    }
}

// -------------------------------------------------------------- threads ----

/// <summary>
/// Threads, largest first. This is the one view that says whether threading
/// is working: a corpus that is all singletons is one that never merged, and
/// a thread holding a dozen unrelated sentences is the failure a consolidator
/// exists to prevent.
/// </summary>
static async Task ShowThreadsAsync(string directory, string? count)
{
    var rows = await Facts(directory).AllAsync(CancellationToken.None);
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
    var log = Facts(directory);
    var rows = await log.AllAsync(CancellationToken.None);

    var into = ResolveThread(rows, keep);
    var from = ResolveThread(rows, absorb);
    if (into is null || from is null || into == from)
    {
        Console.WriteLine(into == from && into is not null ? "Those are the same thread." : "Say more of the thread id — 'threads' lists them.");
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
static async Task SplitThreadAsync(string directory, string prefix)
{
    var log = Facts(directory);
    var rows = await log.AllAsync(CancellationToken.None);
    if (ResolveFact(rows, prefix) is not { } row)
    {
        return;
    }

    // And it is no longer retired: the row that superseded it belonged to
    // the thread this row just left, so the link would leave a fact true and
    // unreadable. The empty string is how a supersession is unset.
    await log.UpdateDerivedAsync([new FactDerived(row.Id, ThreadId: row.Id, SupersededBy: "")], CancellationToken.None);
    Console.WriteLine($"{Oneline(row.Text, 60)} is now its own thread.");
}

/// <summary>A thread id by prefix, or null when the prefix names none or many.</summary>
static string? ResolveThread(IReadOnlyList<Fact> rows, string prefix)
{
    var ids = rows.Where(r => r.ThreadId is not null)
        .Select(r => r.ThreadId!)
        .Distinct(StringComparer.Ordinal)
        .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Take(2)
        .ToList();

    return ids.Count == 1 ? ids[0] : null;
}

// ---------------------------------------------------------- what was said --
//
// Ground truth, and the only store in here with no delete and no edit. Every
// other command in this tool is undone by a backfill; this one would not be.
// The replies are kept in their own shelf and listed separately, because a
// scan of what the person said should not be half the persona's own voice.

static async Task ShowSaidAsync(string directory, string? count, bool replies)
{
    var log = new ParquetUtteranceLog(directory);
    var rows = replies
        ? await log.RepliesAsync(CancellationToken.None)
        : await log.AllAsync(CancellationToken.None);

    if (rows.Count == 0)
    {
        Console.WriteLine(replies ? "Nothing said back yet." : "Nothing said yet.");
        return;
    }

    var take = int.TryParse(count, out var n) && n > 0 ? n : 20;
    Console.WriteLine($"{rows.Count} row(s) over {log.TurnsRecorded} turn(s); newest {Math.Min(take, rows.Count)}:");

    foreach (var u in rows.OrderByDescending(u => u.Timestamp).Take(take))
    {
        Console.WriteLine($"  {u.Id[..8]}  {u.Timestamp:yyyy-MM-dd HH:mm} turn {u.Turn,-5} {u.Speaker,-9} {Oneline(u.Text, 58)}");
    }
}

// ------------------------------------------------------------- passages ----
//
// Where Reflection and Hindsight put what they arrive at: prose the persona
// wrote to itself, one note per event-series, not facts in rows. Live, and
// on its own clock — a passage store that is empty in a busy archive means
// the gate has not opened yet, not that anything is broken.
//
// Shared-tier only by construction — a self-critique belongs to the persona
// rather than to whoever happened to be talking — so no profile argument
// here and no union read.

static ParquetPassageStore Passages(string directory) => new(directory);

static async Task ShowPassagesAsync(string directory, string? countArg)
{
    var all = await Passages(directory).AllAsync(CancellationToken.None);
    if (all.Count == 0)
    {
        Console.WriteLine($"No {ParquetPassageStore.FileName} yet — Reflection and Hindsight have written nothing in this archive.");
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

// Unlike a fact, nothing rebuilds this: a passage is a thing the persona
// thought once, and the model that wrote it will not write the same one
// twice. So the id has to be named in full-ish and the text is echoed as it
// goes, which is the only record left of it afterwards.
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
    Console.WriteLine($"Deleted passage {passage.Id}: {Oneline(passage.Text, 70)}");
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

// ------------------------------------------------------------- internal ----
//
// The pair files. They are no longer where anything the person said is kept —
// the inversion moved that to utterances and facts — but they are not dead
// either: Reflection still files its own records under assistant/reflection,
// the name the persona answers to lives at persona/name, and a fresh archive
// is seeded with a version row at assistant/system.
//
// So there is one flat view rather than the old list/show/showall triple: a
// few pairs holding what the system says about itself does not need a
// browser, it needs to be readable in one screen. An archive from before the
// inversion also has that era's fact rows in here, which is what a stale
// category~topic.parquet beside a live facts/ directory means.
//
// The recency lane (recent.parquet) has no commands at all. It is a derived
// view over these rows with a one-year window, rewritten and trimmed at every
// boot, so deleting from it removes nothing durable and it fills straight
// back in. Delete the pair row instead — that is the copy that lasts.
static async Task<List<(ArchivePair Pair, int Index, ArchiveRecord Row)>> InternalRowsAsync(string directory)
{
    var rows = new List<(ArchivePair, int, ArchiveRecord)>();
    foreach (var pair in ParquetArchiveStore.PairsIn(directory)
        .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.Topic, StringComparer.OrdinalIgnoreCase))
    {
        var records = await ParquetArchiveStore.ReadRecordsAsync(
            ParquetArchiveStore.PairPathFor(directory, pair), CancellationToken.None);
        for (var i = 0; i < records.Count; i++)
        {
            rows.Add((pair, i, records[i]));
        }
    }

    return rows;
}

static async Task ShowInternalAsync(string directory)
{
    var rows = await InternalRowsAsync(directory);
    if (rows.Count == 0)
    {
        Console.WriteLine("No internal rows. A booted host seeds assistant/system with its version.");
        return;
    }

    foreach (var group in rows.GroupBy(r => r.Pair))
    {
        Console.WriteLine($"{group.Key.Category}/{group.Key.Topic} — {group.Count()} row(s)");
        foreach (var (_, index, row) in group)
        {
            Console.WriteLine($"  [{index}] {row.Timestamp:yyyy-MM-dd} {row.Subtopic}/{row.Subject}/{row.Key} = {Oneline(row.Value, 60)}");
        }
    }

    Console.WriteLine("`internal del <category> <topic> <index[,index...]>` takes the indices above.");
}

// Indices are per pair file, which is also the one file a delete is allowed
// to touch — so the numbers here are the ones printed under that pair's
// heading, not a position in the whole listing.
static async Task DeleteInternalAsync(string directory, string category, string topic, string indexList)
{
    var path = ParquetArchiveStore.PairPathFor(directory, new ArchivePair(category, topic));
    if (!File.Exists(path))
    {
        Console.WriteLine($"No pair {category}/{topic}. Try 'internal'.");
        return;
    }

    var records = await ParquetArchiveStore.ReadRecordsAsync(path, CancellationToken.None);
    foreach (var i in indexList.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).OrderByDescending(i => i))
    {
        if (i < 0 || i >= records.Count)
        {
            Console.WriteLine($"Index {i} out of range, skipped.");
            continue;
        }

        Console.WriteLine($"  removed {records[i].Subtopic}/{records[i].Subject}/{records[i].Key} = {Oneline(records[i].Value, 60)}");
        records.RemoveAt(i);
    }

    // An emptied pair loses its file rather than keeping a zero-row one: the
    // file's existence is what puts the pair in the index, so leaving it
    // behind would keep offering a topic with nothing under it.
    if (records.Count == 0)
    {
        File.Delete(path);
    }
    else
    {
        await ParquetArchiveStore.WriteRecordsAsync(path, records, CancellationToken.None);
    }

    Console.WriteLine($"{records.Count} record(s) remain in {category}/{topic}.");
}

static bool IsIndexList(string token) =>
    token.Split(',', StringSplitOptions.RemoveEmptyEntries).All(t => int.TryParse(t, out _));

// --------------------------------------------------------------- upkeep ----

// Backfill, for an archive no host is booting: the same job runs at startup
// against the embedder the host resolved, so a running deployment never needs
// this. What it is still for is the other archive — a copy under test, one
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

/// <summary>
/// Everything, including what was said.
///
/// This was called `reset` and sat in the list next to the harmless commands,
/// from when every row in the archive was a derived one. It is not harmless
/// under the inversion: the utterance log is ground truth, and no backfill
/// puts a sentence back once this has run. So it is named for what it does
/// and asks for the word — `facts clear` is what an operator reaching for
/// this usually wants.
/// </summary>
static async Task EraseAsync(string directory)
{
    var said = (await new ParquetUtteranceLog(directory).AllAsync(CancellationToken.None)).Count;
    Console.WriteLine($"This deletes every parquet under {Path.GetFullPath(directory)}, including {said} utterance(s) that nothing can rebuild.");
    Console.Write("Type 'erase' to confirm: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "erase", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Left alone.");
        return;
    }

    foreach (var file in Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories))
    {
        File.Delete(file);
    }

    // The utterance log's turn counter is not a parquet file, and it is the
    // denominator of every hit rate in it. Left behind, an erased archive
    // reports a thousand turns and no rows, which is a rate of zero over a
    // corpus that never existed.
    var turns = Path.Combine(directory, ParquetUtteranceLog.DirectoryName, ParquetUtteranceLog.TurnCountFileName);
    if (File.Exists(turns))
    {
        File.Delete(turns);
    }

    // One record, not a migration: an empty archive gets the one fact that is
    // true before anything has been said, which is what build is running.
    //
    // Notably NOT its own name. A persona that boots already knowing what it
    // is called cannot be introduced to anyone and cannot be renamed either.
    var record = new ArchiveRecord(
        AssistantScope.Name, AssistantScope.System, "eci", "this", "version", "0.1",
        DateTimeOffset.UtcNow, ArchiveDomain.Internal, 1.0);

    await ParquetArchiveStore.WriteRecordsAsync(
        ParquetArchiveStore.PairPathFor(directory, new ArchivePair(AssistantScope.Name, AssistantScope.System)),
        [record],
        CancellationToken.None);

    Console.WriteLine("Erased.");
}

static string Oneline(string text, int width)
{
    var flat = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    return flat.Length <= width ? flat : flat[..width] + "...";
}
