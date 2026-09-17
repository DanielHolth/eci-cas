using EciCas.Agents.Utterances;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The boot re-read: a better model given the turns a weaker one already
/// indexed.
///
/// The claims that matter are all about what happens when it goes wrong,
/// because the happy path is just "the archive got better". A failed call
/// must cost nothing, a pass must converge rather than re-read the archive
/// every morning, and a skip must be loud enough that nobody mistakes it for
/// a pass that found nothing to do.
/// </summary>
public class FactRebuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eci-rebuild-" + Guid.NewGuid().ToString("n"));

    private const string Weak = "qwen3.5-2b";
    private const string Strong = "gpt-5.6-luna";
    private static readonly string[] Replaces = [Weak];

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Stands in for the substrate. A null <c>model</c> is a failed call,
    /// which the real extractor reports exactly this way -- the utterance
    /// verbatim with nobody's name on it.
    /// </summary>
    private sealed class Reader(string? model, bool statesNothing = false) : IFactExtractor
    {
        public List<long> Turns { get; } = [];

        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(Utterance utterance, string? previousReply, CancellationToken cancellationToken)
        {
            Turns.Add(utterance.Turn);
            return Task.FromResult<IReadOnlyList<ExtractedFact>>(
                [statesNothing
                    ? new ExtractedFact(string.Empty, OriginModel: model)
                    : new ExtractedFact($"{utterance.Text} (read properly)", OriginModel: model)]);
        }
    }

    private static Utterance Said(string text, long turn) =>
        new(Guid.NewGuid().ToString("n"), text, DateTimeOffset.UnixEpoch.AddDays(turn), "user", turn);

    private static Fact Indexed(string text, long turn, string? model) => new(
        Id: Guid.NewGuid().ToString("n"),
        Turn: turn,
        Text: text,
        Timestamp: DateTimeOffset.UnixEpoch.AddDays(turn),
        Speaker: "user",
        Keywords: KeywordExtractor.Content(text),
        OriginModel: model);

    private FactBackfill Backfill(IUtteranceLog said, IFactLog facts) =>
        new(said, facts, new VerbatimFactExtractor(), new FactReliabilityScorer(), new StubEmbeddings(),
            Options.Create(new UtteranceOptions()), NullLogger<FactBackfill>.Instance);

    /// <summary>Two turns, one indexed weakly and one indexed well.</summary>
    private async Task<(ParquetUtteranceLog Said, ParquetFactLog Facts)> Archive()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        await said.AppendAsync([Said("the boat is called Vega", 1), Said("Rex is 4", 2)], CancellationToken.None);
        await facts.AppendAsync(
            [Indexed("the boat is called Vega", 1, Weak), Indexed("Rex is 4", 2, Strong)],
            CancellationToken.None);

        return (said, facts);
    }

    [Fact]
    public async Task OnlyTheTurnsAWeakModelWroteAreReadAgain()
    {
        var (said, facts) = await Archive();
        var reader = new Reader(Strong);

        var result = await Backfill(said, facts).RedriveAsync(reader, Replaces, CancellationToken.None);

        Assert.Equal([1], reader.Turns);
        Assert.Equal(1, result.Turns);

        var rows = await facts.AllAsync(CancellationToken.None);
        Assert.Equal("the boat is called Vega (read properly)", Assert.Single(rows, r => r.Turn == 1).Text);
        Assert.Equal("Rex is 4", Assert.Single(rows, r => r.Turn == 2).Text);
    }

    /// <summary>
    /// The pass runs at every boot, so it has to stop finding work. It does,
    /// because the row it writes names the model that wrote it and that model
    /// is not one it may replace.
    /// </summary>
    [Fact]
    public async Task ASecondPassFindsNothingToDo()
    {
        var (said, facts) = await Archive();
        var backfill = Backfill(said, facts);
        await backfill.RedriveAsync(new Reader(Strong), Replaces, CancellationToken.None);

        var again = new Reader(Strong);
        var result = await backfill.RedriveAsync(again, Replaces, CancellationToken.None);

        Assert.Empty(again.Turns);
        Assert.Equal(0, result.Turns);
    }

    /// <summary>
    /// The extractor is forbidden to throw and answers a dead substrate with
    /// the utterance verbatim instead. A rebuild that deleted before it wrote
    /// would take that for an answer and overwrite the archive with
    /// paragraphs, so the call comes first and a nameless result ends the
    /// pass with nothing touched.
    /// </summary>
    [Fact]
    public async Task AFailedCallChangesNothingAndStopsThePass()
    {
        var (said, facts) = await Archive();

        var result = await Backfill(said, facts).RedriveAsync(new Reader(model: null), Replaces, CancellationToken.None);

        Assert.NotNull(result.Stopped);
        Assert.Equal(0, result.Turns);
        Assert.Equal("the boat is called Vega", Assert.Single(await facts.AllAsync(CancellationToken.None), r => r.Turn == 1).Text);
    }

    /// <summary>
    /// An empty sentence is a verdict, not a failure: a stronger reader
    /// saying this turn stated nothing worth keeping. The weak row it
    /// disagrees with goes, and a marker takes its place -- because a turn
    /// with no row at all looks unread, and the gap-fill would buy the same
    /// verdict again at every boot forever.
    /// </summary>
    [Fact]
    public async Task NothingStatedLeavesAMarkerWhereTheWeakRowWas()
    {
        var (said, facts) = await Archive();

        await Backfill(said, facts).RedriveAsync(new Reader(Strong, statesNothing: true), Replaces, CancellationToken.None);

        var rows = await facts.AllAsync(CancellationToken.None);
        var marker = Assert.Single(rows, r => r.Turn == 1);
        Assert.True(marker.IsMarker);
        Assert.Equal(Strong, marker.OriginModel);
        Assert.False(Assert.Single(rows, r => r.Turn == 2).IsMarker);
    }

    /// <summary>
    /// The point of the marker: a question read once is not read again. The
    /// gap-fill looks for turns with no row, and the marker is a row.
    /// </summary>
    [Fact]
    public async Task AMarkedTurnIsNotReadAgainByTheGapFill()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        var asked = Said("how many kids do i have?", 1);
        await said.AppendAsync([asked], CancellationToken.None);
        await facts.AppendAsync([Fact.NothingStated(asked, Strong)], CancellationToken.None);

        var result = await Backfill(said, facts).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Extracted);
    }

    /// <summary>
    /// But a weak model's "nothing here" is still only a weak model's
    /// opinion, and the marker carries who formed it -- so the rebuild
    /// reaches it exactly like a weak fact, which is the reason the verdict
    /// is signed rather than a bare flag.
    /// </summary>
    [Fact]
    public async Task AWeakMarkerIsStillOwedABetterRead()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        var asked = Said("we moved to Bodo in 2019", 1);
        await said.AppendAsync([asked], CancellationToken.None);
        await facts.AppendAsync([Fact.NothingStated(asked, Weak)], CancellationToken.None);

        var reader = new Reader(Strong);
        await Backfill(said, facts).RedriveAsync(reader, Replaces, CancellationToken.None);

        Assert.Equal([1], reader.Turns);
        Assert.False(Assert.Single(await facts.AllAsync(CancellationToken.None)).IsMarker);
    }

    /// <summary>A row nobody signed is read again whatever the list says.</summary>
    [Fact]
    public async Task ARowFromBeforeTheColumnExistedIsAlwaysOwed()
    {
        var said = new ParquetUtteranceLog(_dir);
        var facts = new ParquetFactLog(_dir);
        await said.AppendAsync([Said("the boat is called Vega", 1)], CancellationToken.None);
        await facts.AppendAsync([Indexed("the boat is called Vega", 1, model: null)], CancellationToken.None);

        var reader = new Reader(Strong);
        await Backfill(said, facts).RedriveAsync(reader, [], CancellationToken.None);

        Assert.Equal([1], reader.Turns);
    }

    // ---- The gate ------------------------------------------------------

    private FactRebuild Rebuild(IUtteranceLog said, IFactLog facts, MaintenanceOptions maintenance,
        SubstrateOptions substrates, UtteranceOptions? utterances = null) =>
        new(Backfill(said, facts), new Reader(Strong), facts, Options.Create(maintenance),
            Options.Create(substrates), Options.Create(utterances ?? new UtteranceOptions { ExtractorEnabled = true }),
            NullLogger<FactRebuild>.Instance);

    private static SubstrateOptions Substrates(string keyVariable) => new()
    {
        Providers =
        {
            ["openai"] = new ProviderEndpoint { BaseUrl = "https://example.invalid", ApiKeyEnvironmentVariable = keyVariable },
        },
    };

    [Fact]
    public async Task WithNoKey_NothingIsRewritten()
    {
        var (said, facts) = await Archive();
        var maintenance = new MaintenanceOptions
        {
            Rebuild = new SubstrateAgentEntry { Provider = "openai", Model = Strong },
            Replaces = [.. Replaces],
        };

        var result = await Rebuild(said, facts, maintenance, Substrates("ECI_A_KEY_NOBODY_SET"))
            .RunAsync(CancellationToken.None);

        Assert.Equal("ECI_A_KEY_NOBODY_SET is not set", result.Stopped);
        Assert.Equal("the boat is called Vega", Assert.Single(await facts.AllAsync(CancellationToken.None), r => r.Turn == 1).Text);
    }

    /// <summary>
    /// Energy is not among the reasons. The meter gates what the persona
    /// spends on talking; repairing the index is exactly the work worth doing
    /// when nobody is talking, and this gate has no idea the meter exists.
    /// </summary>
    [Fact]
    public async Task WithNoRebuildConfigured_ItIsASilentSkip()
    {
        var (said, facts) = await Archive();

        var result = await Rebuild(said, facts, new MaintenanceOptions(), Substrates("")).RunAsync(CancellationToken.None);

        Assert.Null(result.Stopped);
        Assert.Equal(0, result.Turns);
    }

    [Fact]
    public async Task WithTheExtractorSwitchedOff_ThereIsNothingToRebuildWith()
    {
        var (said, facts) = await Archive();
        var maintenance = new MaintenanceOptions { Rebuild = new SubstrateAgentEntry { Provider = "openai" } };

        var result = await Rebuild(said, facts, maintenance, Substrates(""),
            new UtteranceOptions { ExtractorEnabled = false }).RunAsync(CancellationToken.None);

        Assert.Contains("switched off", result.Stopped);
    }
}
