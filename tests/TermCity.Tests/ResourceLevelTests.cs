using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class ResourceLevelTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0.5)]
    [InlineData(1.9, 0.95)]
    [InlineData(2, 1)]
    [InlineData(2.1, 1.05)]
    [InlineData(8, 4)]
    public void GrainGaugeMeasuresReserveAgainstTwoWeekTarget(double weeks, double expected)
    {
        var game = TestCity.Flat();
        game.GrainWeeks = weeks;
        Assert.Equal(expected, SidebarReport.GrainLevel(game), 6);
    }

    [Theory]
    [InlineData(70, 100, "red")]
    [InlineData(94, 100, "red")]
    [InlineData(95, 100, "yellow")]
    [InlineData(100, 100, "yellow")]
    [InlineData(104, 100, "yellow")]
    [InlineData(105, 100, "green")]
    [InlineData(150, 100, "green")]
    public void SupplyGaugesUseExactThresholdsAndPreserveSurplus(int supply, int demand, string color)
    {
        double level = SidebarReport.SupplyLevel(new ServiceSupply(supply, demand));
        Assert.Equal(supply / (double)demand, level);
        Assert.Equal(color == "red" ? SidebarReport.BadColor : color == "yellow" ?
            SidebarReport.WarningColor : SidebarReport.GoodColor, SidebarReport.SupplyLevelColor(level));
    }

    [Fact]
    public void NoDemandHasANeutralLevelRatherThanInfiniteSupply() =>
        Assert.Equal(1, SidebarReport.SupplyLevel(new ServiceSupply(300, 0)));
}
