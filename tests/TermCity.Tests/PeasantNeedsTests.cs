using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class PeasantNeedsTests
{
    [Theory]
    [InlineData("No fuel", "Woodlot")]
    [InlineData("No water", "Town Well")]
    [InlineData("Lawlessness", "Watch House")]
    [InlineData("Hunger", "grain imports")]
    [InlineData("Tithes", "taxes")]
    [InlineData("Road access", "edge-connected")]
    public void ComplaintHintsExplainTheRelevantRemedy(string reason, string remedy)
    {
        var message = PeasantNeeds.Describe(new CityIndicators { Complaints = [new(reason, 10)] }, 0)!;
        Assert.Contains(remedy, message.Hint);
        Assert.DoesNotContain(message.Hint, message.Text);
    }

    [Fact]
    public void TopNeedHasVariedRelevantWordingAndDireColor()
    {
        var indicators = new CityIndicators { Complaints = [new("Learning", 3), new("Hunger", 20)] };
        var messages = Enumerable.Range(0, 4).Select(i => PeasantNeeds.Describe(indicators, i)!).ToArray();
        Assert.Equal(4, messages.Select(m => m.Text).Distinct().Count());
        Assert.All(messages, message =>
        {
            Assert.Contains("bread and grain", message.Text);
            Assert.Equal(Rgb.Hex(0xff7777), message.Color);
        });
    }

    [Fact]
    public void MildNeedsAreYellowAndNoNeedsHideTheMessage()
    {
        Assert.Equal(Rgb.Hex(0xffe066), PeasantNeeds.Describe(
            new CityIndicators { Complaints = [new("Learning", 3)] }, 0)!.Color);
        Assert.Null(PeasantNeeds.Describe(CityIndicators.Neutral, 0));
    }

    [Fact]
    public void WeeklyCycleVisitsEveryComplaintAndWraps()
    {
        var indicators = new CityIndicators { Complaints = [new("Learning", 3), new("Hunger", 20), new("Commons", 2)] };
        Assert.Equal("Hunger", PeasantNeeds.Describe(indicators, 0, 0)!.Reason);
        Assert.Equal("Learning", PeasantNeeds.Describe(indicators, 0, 1)!.Reason);
        Assert.Equal("Commons", PeasantNeeds.Describe(indicators, 0, 2)!.Reason);
        Assert.Equal("Hunger", PeasantNeeds.Describe(indicators, 0, 3)!.Reason);
    }
}
