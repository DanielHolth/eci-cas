using EciCas.Host.Energy;

namespace EciCas.Tests.Host;

public class LevelMeterTests
{
    [Fact]
    public void ANewPersona_StartsAtLevelOne_WithTwoXpToLeaveIt()
    {
        var state = new LevelMeter(path: null).Read();

        Assert.Equal(1, state.Level);
        Assert.Equal(0, state.Xp);
        Assert.Equal(2, state.LevelCost);
    }

    [Fact]
    public void ATurnIsWorthAtMostThreeFacts()
    {
        var meter = new LevelMeter(path: null);

        Assert.Equal(3, meter.Award(9).Xp);
    }

    [Theory]
    // Leaving level L costs 2L, so reaching n takes n(n-1) facts.
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(90, 10)]
    public void ReachingLevelN_TakesNTimesNMinusOneFacts(int xp, int expected)
    {
        var meter = new LevelMeter(path: null);
        for (var i = 0; i < xp; i++)
        {
            meter.Award(1);
        }

        Assert.Equal(expected, meter.Read().Level);
    }

    [Fact]
    public async Task TheLevelSurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"level-{Guid.NewGuid():n}.json");
        try
        {
            var meter = new LevelMeter(path);
            for (var i = 0; i < 3; i++)
            {
                meter.Award(2);
            }

            await meter.PersistAsync(CancellationToken.None);

            Assert.Equal(6, new LevelMeter(path).Read().Xp);
            Assert.Equal(3, new LevelMeter(path).Read().Level);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
