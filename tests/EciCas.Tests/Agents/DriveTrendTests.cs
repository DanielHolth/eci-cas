using System.Text.RegularExpressions;

using EciCas.Agents.Impulse;

namespace EciCas.Tests.Agents;

public class DriveTrendTests
{
    [Fact]
    public void WithNoHistory_SaysSoRatherThanImplyingAFlatLine()
    {
        Assert.Contains("No drive states", DriveTrend.Describe([]));
    }

    [Fact]
    public void WithOneState_DoesNotClaimATrendFromASinglePoint()
    {
        var line = DriveTrend.Describe([new DriveVectors()]);

        Assert.Contains("no history to compare", line);
        Assert.DoesNotContain("rising", line);
        Assert.DoesNotContain("falling", line);
    }

    [Fact]
    public void ReadsDirectionFromOldestToNewest_NotTheOtherWayRound()
    {
        // Newest first, as LookupAsync returns them: curiosity has climbed.
        var line = DriveTrend.Describe(
        [
            new DriveVectors(Curiosity: 0.9),
            new DriveVectors(Curiosity: 0.2),
        ]);

        Assert.Contains("engagement rising", line);
    }

    [Fact]
    public void SmallMovementIsSteady_SoNoiseDoesNotReadAsAMood()
    {
        var line = DriveTrend.Describe(
        [
            new DriveVectors(Curiosity: 0.55),
            new DriveVectors(Curiosity: 0.5),
        ]);

        Assert.Contains("engagement steady", line);
    }

    [Fact]
    public void DescribesStateInWords_NeverAsAReadableMeasurement()
    {
        var line = DriveTrend.Describe(
        [
            new DriveVectors(Curiosity: 0.9, Urgency: 0.8),
            new DriveVectors(Curiosity: 0.1, Fatigue: 0.9),
        ]);

        // Reflection is being told how it has been, not handed its telemetry:
        // a drive value in here would invite the persona to quote one back.
        // The count of states is the one number allowed, and it has no decimal.
        Assert.DoesNotMatch(new Regex(@"\d+\.\d+"), line);
    }
}
