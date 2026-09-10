using EciCas.Agents.Utterances;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The inverted archive, end to end and on disk.
///
/// These are the claims the flag rests on: the log survives a restart, a
/// restatement costs one slot rather than two, a thread answers with its
/// newest phrasing, and a retired row stays out of the way without being
/// deleted. Everything else about the inversion is a tuning constant.
/// </summary>
public class UtteranceLogTests : IDisposable
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

    private ThreadWeaver Weaver(IUtteranceLog log, UtteranceOptions? options = null) =>
        new(log, Embeddings(), new NullUtteranceConsolidator(),
            Options.Create(options ?? new UtteranceOptions()), NullLogger<ThreadWeaver>.Instance);

    private static Utterance Said(string text, DateTimeOffset when) => new(
        Id: Guid.NewGuid().ToString("n"),
        Text: text,
        Timestamp: when,
        Speaker: "user",
        ProfileId: "default",
        Keywords: KeywordExtractor.Content(text));

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
    public async Task ARestatementJoinsTheThreadItRestates()
    {
        var log = new ParquetUtteranceLog(_dir);
        var weaver = Weaver(log);

        var first = await weaver.WeaveAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow.AddDays(-2))], CancellationToken.None);
        await log.AppendAsync(first.Rows, CancellationToken.None);

        var again = await weaver.WeaveAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.AppendAsync(again.Rows, CancellationToken.None);

        var rows = await log.AllAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.ThreadId).Distinct());
    }

    [Fact]
    public async Task ADifferentSubjectMintsItsOwnThread()
    {
        var log = new ParquetUtteranceLog(_dir);
        var weaver = Weaver(log);

        var first = await weaver.WeaveAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow.AddDays(-2))], CancellationToken.None);
        await log.AppendAsync(first.Rows, CancellationToken.None);

        var other = await weaver.WeaveAsync([Said("my sister moved to Tromso last winter", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.AppendAsync(other.Rows, CancellationToken.None);

        var rows = await log.AllAsync(CancellationToken.None);
        Assert.Equal(2, rows.Select(r => r.ThreadId).Distinct().Count());
    }

    [Fact]
    public async Task AThreadAnswersWithItsNewestMember()
    {
        var log = new ParquetUtteranceLog(_dir);
        var old = Said("the boat is called Vega", DateTimeOffset.UtcNow.AddYears(-3)) with { ThreadId = "t1", Embedding = Vector("the boat is called Vega"), EmbeddingModelId = "stub-bow" };
        var now = Said("the boat is called Vega now", DateTimeOffset.UtcNow) with { ThreadId = "t1", Embedding = Vector("the boat is called Vega now"), EmbeddingModelId = "stub-bow" };
        await log.AppendAsync([old, now], CancellationToken.None);

        var consult = new UtteranceConsult(log, Embeddings(), Options.Create(new UtteranceOptions()), new RuntimeKnobs());
        var hits = await consult.FindAsync("the boat is called Vega", CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(now.Id, hit.Row.Id);
    }

    [Fact]
    public async Task ARetiredRowLosesToTheOneThatReplacedIt()
    {
        var log = new ParquetUtteranceLog(_dir);
        var stale = Said("the boat is called Vega", DateTimeOffset.UtcNow.AddYears(-3)) with { ThreadId = "t1", Embedding = Vector("the boat is called Vega"), EmbeddingModelId = "stub-bow" };
        var fresh = Said("the boat is called Nordlys", DateTimeOffset.UtcNow) with { ThreadId = "t2", Embedding = Vector("the boat is called Nordlys"), EmbeddingModelId = "stub-bow" };
        await log.AppendAsync([stale, fresh], CancellationToken.None);
        await log.UpdateDerivedAsync([new UtteranceDerived(stale.Id, SupersededBy: fresh.Id)], CancellationToken.None);

        var consult = new UtteranceConsult(log, Embeddings(), Options.Create(new UtteranceOptions()), new RuntimeKnobs());
        var hits = await consult.FindAsync("what is the boat called", CancellationToken.None);

        // The retired row may still be topped up by the unrestricted pass --
        // that is the second read's whole purpose -- but the row that
        // replaced it answers first.
        Assert.NotEmpty(hits);
        Assert.Equal(fresh.Id, hits[0].Row.Id);
        Assert.True(hits[0].Current);
    }

    [Fact]
    public async Task TheBackfillGivesABareRowAVectorAndAThread()
    {
        var log = new ParquetUtteranceLog(_dir);
        var when = DateTimeOffset.UtcNow;

        // What a turn taken while the embedder was down leaves behind: good
        // ground truth, invisible to a sweep.
        await log.AppendAsync([
            Said("the boat is called Vega", when.AddDays(-1)),
            Said("the boat is called Vega", when),
        ], CancellationToken.None);

        var backfill = new UtteranceBackfill(log, Embeddings(), Options.Create(new UtteranceOptions()));
        var (embedded, threaded) = await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(2, embedded);
        Assert.Equal(2, threaded);

        var rows = await log.AllAsync(CancellationToken.None);
        Assert.All(rows, r => Assert.True(r.HasVector("stub-bow")));

        // Restatements of one fact, so one thread -- the free half of the
        // write-time rule, applied without a call.
        Assert.Single(rows.Select(r => r.ThreadId).Distinct());
    }

    [Fact]
    public async Task TheBackfillIsFreeOnAWarmLog()
    {
        var log = new ParquetUtteranceLog(_dir);
        var weaver = Weaver(log);
        var woven = await weaver.WeaveAsync([Said("the boat is called Vega", DateTimeOffset.UtcNow)], CancellationToken.None);
        await log.AppendAsync(woven.Rows, CancellationToken.None);

        var backfill = new UtteranceBackfill(log, Embeddings(), Options.Create(new UtteranceOptions()));

        Assert.Equal((0, 0), await backfill.RunAsync(CancellationToken.None));
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
        var log = new ParquetUtteranceLog(_dir);
        var consult = new UtteranceConsult(log, Embeddings(), Options.Create(new UtteranceOptions()), new RuntimeKnobs());

        Assert.Empty(await consult.FindAsync("anything at all", CancellationToken.None));
    }
}
