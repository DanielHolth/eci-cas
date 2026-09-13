using EciCas.Core;

namespace EciCas.Tests.Agents;

/// <summary>
/// The two pieces of the provenance columns that decide something rather
/// than just carrying a value: which rows a rebuild claims, and what happens
/// to a class word nobody agreed to.
/// </summary>
public sealed class FactProvenanceTests
{
    private static Fact Row(string? origin) =>
        new("id", "src", "text", DateTimeOffset.UnixEpoch, "Daniel", [], OriginModel: origin);

    /// <summary>
    /// The whole reason the first rebuild needs no migration and no flag: a
    /// row written before the column existed cannot prove it came from a
    /// capable model, so it is treated as though it did not. Guess weak, and
    /// the archive converges on its own.
    /// </summary>
    [Fact]
    public void ARowWithNoRecordedOrigin_IsClaimedByTheRebuild()
    {
        string[] local = ["qwen3.5-2b"];

        Assert.True(Row(null).WrittenBy(local));
        Assert.True(Row("qwen3.5-2b").WrittenBy(local));
        Assert.False(Row("gpt-5.6-luna").WrittenBy(local));
    }

    /// <summary>
    /// The valve. A model that invents a word gets "other", not a column
    /// nothing downstream can group by -- and casing and stray whitespace
    /// are the writer's habits, not a new class.
    /// </summary>
    [Fact]
    public void AClassNobodyAgreedTo_LandsInTheValve()
    {
        Assert.Equal("date", FactClasses.Normalise("Date"));
        Assert.Equal("state", FactClasses.Normalise("  state "));
        Assert.Equal(FactClasses.Other, FactClasses.Normalise("vibe"));
        Assert.Equal(FactClasses.Other, FactClasses.Normalise(null));
    }
}
