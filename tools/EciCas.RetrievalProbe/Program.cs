using System.Text.Json;
using EciCas.Agents.Recall;
using EciCas.Core;
using EciCas.RetrievalProbe;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Throwaway measurement rig, not a product. It answers one question before
// the retrieval rewrite is worth starting: does cosine over pre-embedded fact
// rows actually find the row a real question is asking about? Nothing here
// touches the bus, and no agent knows it exists.
//
// Three representations of the same row are embedded and scored separately,
// because the interesting failure is not "vectors don't work" but "vectors
// don't work on paths" — a distinction that decides whether the fix is a
// different architecture or a different string.

var args_ = Args.Parse(args);
if (args_ is null)
{
    Console.Error.WriteLine("""
        usage: dotnet run --project tools/EciCas.RetrievalProbe -- \
            --archive <dir> --questions <file.json> \
            --model <model.onnx> --vocab <vocab.txt> \
            [--glosses <file.json>] [--query-prefix "query: "] [--passage-prefix "passage: "]

        questions.json: [ { "ask": "what is my son called?", "expect": "person/family/son/marcus holth/name" } ]
        glosses.json:   { "person/family/son/marcus holth/name": "what my son is called, his first name" }
        """);
    return 1;
}

var embeddings = new OnnxEmbeddingProvider(
    Options.Create(new EmbeddingOptions
    {
        Provider = "onnx",
        ModelPath = args_.ModelPath,
        VocabPath = args_.VocabPath,
    }),
    NullLogger<OnnxEmbeddingProvider>.Instance);

if (!embeddings.Available)
{
    Console.Error.WriteLine($"No embedding model at {args_.ModelPath} — run ./scripts/get-embedding-model.ps1 first.");
    return 1;
}

// Shared categories left at the default: the probe reads the shared tier
// only, which is every row a profile-less turn would see.
var store = new ParquetArchiveStore(args_.ArchiveDirectory, null);
var pairs = store.IndexFor(null);
var rows = new List<ArchiveRecord>();
foreach (var pair in pairs)
{
    rows.AddRange(await store.LookupAsync(pair, null, CancellationToken.None));
}

if (rows.Count == 0)
{
    Console.Error.WriteLine($"No rows under {args_.ArchiveDirectory} — point --archive at a real archive, not a fresh build output.");
    return 1;
}

var questions = JsonSerializer.Deserialize<List<Question>>(File.ReadAllText(args_.QuestionsPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

var glosses = args_.GlossesPath is null
    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    : new Dictionary<string, string>(
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args_.GlossesPath)) ?? [],
        StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"{rows.Count} row(s) across {pairs.Count} pair(s), {questions.Count} question(s), model {embeddings.ModelId}");
Console.WriteLine();

// The row as Recall's picking prompt renders it — the honest baseline,
// because it is the string the current design already reasons over.
static string Raw(ArchiveRecord r) => $"{r.Subtopic} / {r.Subject} {r.Key} = {r.Value}";

// The full address, on the theory that category and topic carry signal the
// picking prompt withholds only because scope already implied them.
static string Path_(ArchiveRecord r) => $"{Address(r)} = {r.Value}";

static string Address(ArchiveRecord r) => $"{r.Category}/{r.Topic}/{r.Subtopic}/{r.Subject}/{r.Key}";

var representations = new List<(string Name, Func<ArchiveRecord, string?> Render)>
{
    ("raw", r => Raw(r)),
    ("path", r => Path_(r)),
};

if (glosses.Count > 0)
{
    // Missing glosses render null and drop the row from that run entirely,
    // rather than silently falling back to the path — a gloss run scored
    // against half-path documents would measure neither.
    representations.Add(("gloss", r => glosses.TryGetValue(Address(r), out var g) ? g : null));
}

var addresses = rows.Select(Address).ToList();
var queryVectors = await embeddings.EmbedAsync(
    [.. questions.Select(q => args_.QueryPrefix + q.Ask)], CancellationToken.None);

var results = new List<(string Name, Summary Summary, List<string> Misses)>();

foreach (var (name, render) in representations)
{
    var kept = rows.Select((r, i) => (Text: render(r), Address: addresses[i]))
        .Where(x => !string.IsNullOrWhiteSpace(x.Text))
        .ToList();

    var docVectors = await embeddings.EmbedAsync(
        [.. kept.Select(x => args_.PassagePrefix + x.Text!)], CancellationToken.None);

    var ranks = new List<int>();
    var misses = new List<string>();

    for (var q = 0; q < questions.Count; q++)
    {
        var ranked = kept
            .Select((x, i) => (x.Address, Score: VectorMath.Cosine(queryVectors[q], docVectors[i])))
            .OrderByDescending(x => x.Score)
            .ToList();

        var rank = Scoring.RankOf([.. ranked.Select(x => x.Address)], questions[q].Expect);
        ranks.Add(rank);

        if (rank != 0)
        {
            var top = ranked[0];
            misses.Add($"    \"{questions[q].Ask}\"\n      want {questions[q].Expect} (rank {(rank < 0 ? "absent" : (rank + 1).ToString())})\n      got  {top.Address} ({top.Score:F3})");
        }
    }

    results.Add((name, Scoring.Summarize(ranks), misses));
}

Console.WriteLine($"{"representation",-16} {"hit@1",8} {"hit@3",8} {"MRR",8}");
foreach (var (name, summary, _) in results)
{
    Console.WriteLine($"{name,-16} {summary.Hit1,8:P0} {summary.Hit3,8:P0} {summary.Mrr,8:F3}");
}

foreach (var (name, _, misses) in results.Where(r => r.Misses.Count > 0))
{
    Console.WriteLine();
    Console.WriteLine($"  {name} — {misses.Count} question(s) not ranked first:");
    foreach (var miss in misses)
    {
        Console.WriteLine(miss);
    }
}

return 0;

internal sealed record Question(string Ask, string Expect);

internal sealed record Args(
    string ArchiveDirectory,
    string QuestionsPath,
    string ModelPath,
    string VocabPath,
    string? GlossesPath,
    string QueryPrefix,
    string PassagePrefix)
{
    public static Args? Parse(string[] argv)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < argv.Length; i += 2)
        {
            map[argv[i].TrimStart('-')] = argv[i + 1];
        }

        if (!map.TryGetValue("archive", out var archive) ||
            !map.TryGetValue("questions", out var questions) ||
            !map.TryGetValue("model", out var model) ||
            !map.TryGetValue("vocab", out var vocab))
        {
            return null;
        }

        return new Args(
            System.IO.Path.GetFullPath(archive),
            System.IO.Path.GetFullPath(questions),
            System.IO.Path.GetFullPath(model),
            System.IO.Path.GetFullPath(vocab),
            map.TryGetValue("glosses", out var g) ? System.IO.Path.GetFullPath(g) : null,
            map.GetValueOrDefault("query-prefix", ""),
            map.GetValueOrDefault("passage-prefix", ""));
    }
}
