using EciCas.Agents.Utterances;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The split archive, end to end and on disk.
///
/// Two stores now: what was said, which is never rewritten, and the facts read
/// out of it, which are entirely disposable. These are the claims that rest
/// on: what was said survives a restart, a long paste becomes one row per
/// fact, a restatement costs one slot rather than two, a thread answers with
/// its newest phrasing, a retired row stays out of the way without being
/// deleted, and the whole index can be thrown away and rebuilt from the
/// utterances alone.
/// </summary>
public class FactLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eci-utter-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A bag-of-words vector over a fixed alphabet, L2-normalised. Crude on
    /// purpose: cosine between two texts is then the overlap of their
    /// letters, which is enough to make "the same sentence again" land above
    /// any threshold and "a different subject" land below one, without
    /// pretending to be an embedding model.
    /// </summary>
    private static float[] Vector(string text)
    {
        var v = new float[26];
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z')
            {
                v[c - 'a']++;
            }
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : [.. v.Select(x => x / norm)];
    }

    private static StubEmbeddings Embeddings() => new(Vector, "stub-bow");

    private ThreadWeaver Weaver(IFactLog log, UtteranceOptions? options = null) =>
        new(log, Embeddings(), new NullFactConsolidator(),
            Options.Create(options ?? new UtteranceOptions()), NullLogger<ThreadWeaver>.Instance);

    private static Utterance Said(string text, DateTimeOffset when) => new(
        Id: Guid.NewGuid().ToString("n"),
        Text: text,
        Timestamp: when,
        Speaker: "user");

    private static Fact Read(string text, DateTimeOffset when) => new(
        Id: Guid.NewGuid().ToString("n"),
        SourceId: Guid.NewGuid().ToString("n"),
        Text: text,
        Timestamp: when,
        Speaker: "user",
        Keywords: KeywordExtractor.Content(text));

    private static Fact Embedded(Fact fact, string thread) =>
        fact with { ThreadId = thread, Embedding = Vector(fact.Text), EmbeddingModelId = "stub-bow" };

    /// <summary>
    /// One line in, one fact per sentence out. Stands in for the substrate
    /// extractor so the tests around it cost nothing and never vary.
    /// </summary>
    private sealed class SentenceExtractor : IFactExtractor
    {
        public Task<IReadOnlyList<string>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. utterance.Text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    private FactBackfill Backfill(IUtteranceLog said, IFactLog facts, IFactExtractor? extractor = null) =>
        new(said, facts, extractor ?? new VerbatimFactExtractor(), Embeddings(),
            Options.Create(new UtteranceOptions()), NullLogger<FactBackfill>.Instance);

    [Fact]
    public async Task WhatWasSaidSurvivesARestart()
    {
        var log = new ParquetUtteranceLog(_dir);
        await log.AppendAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.RecordTurnAsync(CancellationToken.None);

        // A second instance over the same directory is what a restart is.
        var reopened = new ParquetUtteranceLog(_dir);
        var rows = await reopened.AllAsync(CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal("the boat is called Vega", rows[0].Text);
        Assert.Equal(1, reopened.TurnsRecorded);
    }

    [Fact]
    public async Task RepliesKeepTheirTurnAndPairWithTheNextInput()
    {
        var log = new ParquetUtteranceLog(_dir);
        await log.AppendReplyAsync(new Utterance("r1", "The first knob is Tier.", DateTimeOffset.UtcNow, "assistant", 1), CancellationToken.None);
        Assert.Equal(1, await log.RecordTurnAsync(CancellationToken.None));

        var reopened = new ParquetUtteranceLog(_dir);
        var replies = await reopened.RepliesAsync(CancellationToken.None);
        Assert.Equal(1, Assert.Single(replies).Turn);

        var next = new Utterance("u2", "I totally agree.", DateTimeOffset.UtcNow, "user", reopened.TurnsRecorded + 1);
        Assert.Equal("The first knob is Tier.", UtteranceContext.PreviousReply(replies, next));
        Assert.Null(UtteranceContext.PreviousReply(replies, next with { Turn = 1 }));
    }

    /// <summary>
    /// The shelf's boot backfill shares this root. It once swept it
    /// recursively, read these shards as shelf rows, and rewrote them with
    /// only Timestamp intact.
    /// </summary>
    [Fact]
    public async Task TheShelfBackfillLeavesWhatWasSaidAlone()
    {
        await new ParquetUtteranceLog(_dir).AppendAsync(
            [Said("Maria Benita was born on 10.01.2011", DateTimeOffset.UtcNow)], CancellationToken.None);

        await EciCas.Agents.Recall.ArchiveBackfill.RunAsync(_dir, Embeddings(), onFile: null, CancellationToken.None);

        var rows = await new ParquetUtteranceLog(_dir).AllAsync(CancellationToken.None);
        Assert.Equal("Maria Benita was born on 10.01.2011", Assert.Single(rows).Text);
    }

    [Fact]
    public async Task ALongPasteBecomesOneRowPerFact()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        var utterance = Said("the boat is called Vega. my sister moved to Tromso. Rex is 4", DateTimeOffset.UtcNow);
        await said.AppendAsync([utterance], CancellationToken.None);

        await Backfill(said, facts, new SentenceExtractor()).RunAsync(CancellationToken.None);

        // Three facts, and every one of them points back at the single line
        // they were read out of -- that link is the only thing tying the
        // disposable half to the half that is not.
        var rows = await facts.AllAsync(CancellationToken.None);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(utterance.Id, r.SourceId));
        Assert.Single(await said.AllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARestatementJoinsTheThreadItRestates()
    {
        var log = new ParquetFactLog(_dir);
        var weaver = Weaver(log);

        var first = await weaver.WeaveAsync([Read("the boat is called Vega", DateTimeOffset.UtcNow.AddDays(-2))], CancellationToken.None);
        await log.AppendAsync(first.Rows, CancellationToken.None);

        var again = await weaver.WeaveAsync([Read("the boat is called Vega", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.AppendAsync(again.Rows, CancellationToken.None);

        var rows = await log.AllAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.ThreadId).Distinct());
    }

    [Fact]
    public async Task ADifferentSubjectMintsItsOwnThread()
    {
        var log = new ParquetFactLog(_dir);
        var weaver = Weaver(log);

        var first = await weaver.WeaveAsync([Read("the boat is called Vega", DateTimeOffset.UtcNow.AddDays(-2))], CancellationToken.None);
        await log.AppendAsync(first.Rows, CancellationToken.None);

        var other = await weaver.WeaveAsync([Read("my sister moved to Tromso last winter", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.AppendAsync(other.Rows, CancellationToken.None);

        var rows = await log.AllAsync(CancellationToken.None);
        Assert.Equal(2, rows.Select(r => r.ThreadId).Distinct().Count());
    }

    [Fact]
    public async Task AThreadAnswersWithItsNewestMember()
    {
        var log = new ParquetFactLog(_dir);
        var old = Embedded(Read("the boat is called Vega", DateTimeOffset.UtcNow.AddYears(-3)), "t1");
        var now = Embedded(Read("the boat is called Vega now", DateTimeOffset.UtcNow), "t1");
        await log.AppendAsync([old, now], CancellationToken.None);

        var consult = new FactConsult(log, new ParquetUtteranceLog(_dir), Embeddings(),
            Options.Create(new UtteranceOptions()), new RuntimeKnobs());
        var hits = await consult.FindAsync("the boat is called Vega", CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(now.Id, hit.Row.Id);
    }

    [Fact]
    public async Task ARetiredRowLosesToTheOneThatReplacedIt()
    {
        var log = new ParquetFactLog(_dir);
        var stale = Embedded(Read("the boat is called Vega", DateTimeOffset.UtcNow.AddYears(-3)), "t1");
        var fresh = Embedded(Read("the boat is called Nordlys", DateTimeOffset.UtcNow), "t2");
        await log.AppendAsync([stale, fresh], CancellationToken.None);
        await log.UpdateDerivedAsync([new FactDerived(stale.Id, SupersededBy: fresh.Id)], CancellationToken.None);

        var consult = new FactConsult(log, new ParquetUtteranceLog(_dir), Embeddings(),
            Options.Create(new UtteranceOptions()), new RuntimeKnobs());
        var hits = await consult.FindAsync("what is the boat called", CancellationToken.None);

        // The retired row may still be topped up by the unrestricted pass --
        // that is the second read's whole purpose -- but the row that
        // replaced it answers first.
        Assert.NotEmpty(hits);
        Assert.Equal(fresh.Id, hits[0].Row.Id);
        Assert.True(hits[0].Current);
    }

    [Fact]
    public async Task CorrectingARowRewritesItAndDropsItsStaleVector()
    {
        var log = new ParquetFactLog(_dir);
        var invented = Embedded(Read("I moved to Bodo in 2019", DateTimeOffset.UtcNow), "t1");
        await log.AppendAsync([invented], CancellationToken.None);

        Assert.True(await log.ReviseAsync(invented.Id, "I moved to Tromso in 2019", CancellationToken.None));

        var row = Assert.Single(await log.AllAsync(CancellationToken.None));
        Assert.Equal("I moved to Tromso in 2019", row.Text);
        // The vector pointed at the sentence that was never said; leaving it
        // would keep retrieving the row for the wrong question.
        Assert.Null(row.Embedding);
        Assert.Contains("tromso", row.Keywords.Select(k => k.ToLowerInvariant()));
    }

    [Fact]
    public async Task CorrectingARowThatIsNotThereChangesNothing()
    {
        var log = new ParquetFactLog(_dir);

        Assert.False(await log.ReviseAsync("nobody", "something", CancellationToken.None));
    }

    [Fact]
    public async Task RemovingARowTakesItOutAndUnretiresWhateverItReplaced()
    {
        var log = new ParquetFactLog(_dir);
        var stale = Embedded(Read("the boat is called Vega", DateTimeOffset.UtcNow.AddYears(-3)), "t1");
        var invented = Embedded(Read("the boat is called Nordlys", DateTimeOffset.UtcNow), "t1");
        await log.AppendAsync([stale, invented], CancellationToken.None);
        await log.UpdateDerivedAsync([new FactDerived(stale.Id, SupersededBy: invented.Id)], CancellationToken.None);

        Assert.Equal(1, await log.RemoveAsync([invented.Id], CancellationToken.None));

        var row = Assert.Single(await log.AllAsync(CancellationToken.None));
        Assert.Equal(stale.Id, row.Id);
        // Retired by a row that turned out to be made up: the older row is
        // the truth again, not a fact superseded by nothing.
        Assert.Null(row.SupersededBy);
    }

    [Fact]
    public async Task TheBackfillGivesABareRowAVectorAndAThread()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        var when = DateTimeOffset.UtcNow;

        // What a turn taken while the embedder was down leaves behind: good
        // ground truth, invisible to a sweep.
        await said.AppendAsync([
            Said("the boat is called Vega", when.AddDays(-1)),
            Said("the boat is called Vega", when),
        ], CancellationToken.None);

        var result = await Backfill(said, facts).RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Extracted);
        Assert.Equal(2, result.Embedded);
        Assert.Equal(2, result.Threaded);

        var rows = await facts.AllAsync(CancellationToken.None);
        Assert.All(rows, r => Assert.True(r.HasVector("stub-bow")));

        // Restatements of one fact, so one thread -- the free half of the
        // write-time rule, applied without a call.
        Assert.Single(rows.Select(r => r.ThreadId).Distinct());
    }

    [Fact]
    public async Task TheBackfillIsFreeOnAWarmLog()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        await said.AppendAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow)], CancellationToken.None);

        var backfill = Backfill(said, facts);
        await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(new FactBackfill.Result(0, 0, 0), await backfill.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheIndexCanBeThrownAwayAndRebuiltFromWhatWasSaid()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        await said.AppendAsync([
            Said("the boat is called Vega. Rex is 4", DateTimeOffset.UtcNow.AddDays(-1)),
            Said("my sister moved to Tromso", DateTimeOffset.UtcNow),
        ], CancellationToken.None);

        var backfill = Backfill(said, facts, new SentenceExtractor());
        await backfill.RunAsync(CancellationToken.None);
        var before = (await facts.AllAsync(CancellationToken.None)).Select(f => f.Text).OrderBy(t => t).ToList();

        var rebuilt = await backfill.RebuildAsync(CancellationToken.None);
        var after = (await facts.AllAsync(CancellationToken.None)).Select(f => f.Text).OrderBy(t => t).ToList();

        // Same facts, new ids: the index is a computation over the log, and
        // this is the claim that makes it disposable.
        Assert.Equal(before, after);
        Assert.Equal(2, rebuilt.Extracted);
        Assert.Equal(2, (await said.AllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public void FillerEarnsNoRowAndAShortFactStillDoes()
    {
        var options = new UtteranceOptions();

        Assert.False(UtteranceFilter.Keep(KeywordExtractor.Content("haha ok"), options));
        Assert.False(UtteranceFilter.Keep(KeywordExtractor.Content("hmm, ok -- yeah, haha"), options));

        // The cut has to be content words: both of these are shorter than the
        // filler above and both carry a claim.
        Assert.True(UtteranceFilter.Keep(KeywordExtractor.Content("Rex is 4"), options));
        Assert.True(UtteranceFilter.Keep(KeywordExtractor.Content("Vega"), options));

        // Zero is the behaviour before the filter existed.
        Assert.True(UtteranceFilter.Keep(KeywordExtractor.Content("haha ok"), new UtteranceOptions { MinContentWords = 0 }));
    }

    [Fact]
    public async Task AReadOnAnEmptyLogIsNotAnError()
    {
        var consult = new FactConsult(new ParquetFactLog(_dir), new ParquetUtteranceLog(_dir), Embeddings(),
            Options.Create(new UtteranceOptions()), new RuntimeKnobs());

        Assert.Empty(await consult.FindAsync("anything at all", CancellationToken.None));
    }
}
