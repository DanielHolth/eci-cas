using EciCas.Agents.Recall;
using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// Salience is a read-time comparison key and nothing else: no write depends
/// on it, so what these cover is the ordering it produces. The three terms
/// pull in different directions on purpose -- importance says what was worth
/// keeping, age says it may have stopped mattering, and use says the turns
/// disagree -- and each test isolates one of them.
/// </summary>
public class ArchiveSalienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"eci-salience-{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static ArchiveRecord Row(string key, double importance, int ageDays, long hits = 0, int? lastHitDays = null) =>
        new("person", "daniel", "home", "daniel", key, "value", Now.AddDays(-ageDays),
            Importance: importance, Domain: "user")
        {
            Hits = hits,
            LastHitAt = lastHitDays is { } d ? Now.AddDays(-d) : null,
        };

    private static double Effective(ArchiveRecord record, long turns = 0, double halfLife = 60, double weight = 0.3) =>
        ArchiveSalience.Effective(record, Now, turns, halfLife, weight);

    [Fact]
    public void AtOneHalfLife_ImportanceIsHalved() =>
        Assert.Equal(0.4, Effective(Row("a", 0.8, ageDays: 60)), precision: 6);

    /// <summary>
    /// The knob has to be able to buy back exactly today's behaviour, or it
    /// is not a knob -- it is a change of ranking with an escape hatch that
    /// does not quite escape.
    /// </summary>
    [Fact]
    public void WithTheHalfLifeOff_SalienceIsImportance() =>
        Assert.Equal(0.8, Effective(Row("a", 0.8, ageDays: 4000), halfLife: 0), precision: 6);

    [Fact]
    public void AFadedFactLosesToAFreshOne() =>
        Assert.True(Effective(Row("old", 0.9, ageDays: 365)) < Effective(Row("new", 0.4, ageDays: 1)));

    /// <summary>
    /// Being read counts as being touched. This is the line between
    /// forgetting a fact and merely having known it a while: the birthday
    /// asked for every year is not stale on the day it is asked.
    /// </summary>
    [Fact]
    public void AFactThatKeepsBeingRecalledDoesNotFade()
    {
        var forgotten = Row("old", 0.9, ageDays: 365);
        var recalled = Row("old", 0.9, ageDays: 365, hits: 3, lastHitDays: 1);

        Assert.True(Effective(recalled, turns: 100) > Effective(forgotten, turns: 100));
        Assert.True(Effective(recalled, turns: 100) > 0.88);
    }

    /// <summary>
    /// Additive, not multiplicative: a row nobody has ever asked for keeps
    /// its whole decayed importance rather than being zeroed, which is what
    /// makes an archive that has never counted a hit rank as it always did.
    /// </summary>
    [Fact]
    public void ARowNobodyHasAskedForIsNotZeroed() =>
        Assert.Equal(Effective(Row("a", 0.5, ageDays: 0)), 0.5, precision: 6);

    [Fact]
    public void TheHitLiftIsCapped()
    {
        // More hits than turns -- a row credited in every one of its
        // profile's turns plus the shared tier's copies -- must not run away
        // with the ranking.
        var record = Row("a", 0.1, ageDays: 0, hits: 500, lastHitDays: 0);
        Assert.Equal(0.4, Effective(record, turns: 10), precision: 6);
    }

    /// <summary>
    /// Hits survive a restatement. "Lives in Oslo" becoming "lives in Bergen"
    /// replaces the row at its address, and the address is what has been
    /// asked about -- the count belongs to the question, not to the answer it
    /// happened to hold.
    /// </summary>
    [Fact]
    public async Task RestatingAFactKeepsItsHitHistory()
    {
        var store = new ParquetArchiveStore(_directory);
        var pair = new ArchivePair("person", "daniel");
        await store.WriteAsync([Row("city", 0.5, ageDays: 1)], null, CancellationToken.None);

        var stored = await store.LookupAsync(pair, null, CancellationToken.None);
        await store.RecordRecallAsync(stored, null, CancellationToken.None);
        await store.WriteAsync([Row("city", 0.5, ageDays: 0)], null, CancellationToken.None);

        var after = Assert.Single(await store.LookupAsync(pair, null, CancellationToken.None));
        Assert.Equal(1, after.Hits);
        Assert.NotNull(after.LastHitAt);
    }

    /// <summary>
    /// The denominator counts turns, not recalls: a hundred questions that
    /// needed no fact are exactly what makes the two rows that were used
    /// look valuable.
    /// </summary>
    [Fact]
    public async Task ATurnThatRecalledNothingStillCounts()
    {
        var store = new ParquetArchiveStore(_directory);

        await store.RecordRecallAsync([], null, CancellationToken.None);
        await store.RecordRecallAsync([], null, CancellationToken.None);

        Assert.Equal(2, store.TurnsRecorded);
        Assert.Equal(2, new ParquetArchiveStore(_directory).TurnsRecorded);
    }

    /// <summary>
    /// Crediting reaches the lane's copy too. The lane holds copies of pair
    /// rows, and a caller handed a union does not know which side a recalled
    /// row came from -- if only one side were credited the two would drift
    /// and the same fact would rank differently depending on how it surfaced.
    /// </summary>
    [Fact]
    public async Task BothTheDrawerAndTheLaneAreCredited()
    {
        var store = new ParquetArchiveStore(_directory);
        await store.WriteAsync([Row("city", 0.5, ageDays: 0)], null, CancellationToken.None);
        var stored = await store.LookupAsync(new ArchivePair("person", "daniel"), null, CancellationToken.None);

        await store.RecordRecallAsync(stored, null, CancellationToken.None);

        store.Invalidate();
        var fresh = new ParquetArchiveStore(_directory);
        Assert.Equal(1, Assert.Single(await fresh.LookupAsync(new ArchivePair("person", "daniel"), null, CancellationToken.None)).Hits);
        Assert.Equal(1, Assert.Single(await fresh.RecentAsync(null, 10, CancellationToken.None)).Hits);
    }
}
