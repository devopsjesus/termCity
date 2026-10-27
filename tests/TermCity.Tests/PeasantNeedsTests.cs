using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
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

    [Theory]
    [InlineData("Slow journeys")]
    [InlineData("Road access")]
    public void RetiredRoadComplaintsNeverAppearInPetitions(string reason)
    {
        var indicators = new CityIndicators { Complaints = [new(reason, 20)] };
        Assert.Null(PeasantNeeds.Describe(indicators, 0));
        indicators = indicators with { Complaints = [new(reason, 20), new("Learning", 3)] };
        for (int i = 0; i < 4; i++)
            Assert.Equal("Learning", PeasantNeeds.Describe(indicators, i, i)!.Reason);
    }

    [Fact]
    public void RoadlessHomesHaveNoJourneyPenaltyAndKeepTheSameHappiness()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var map = game.Map;
        var home = map.Content.Buildings.Get("Cottage");
        foreach (int y in new[] { 5, 19 })
        {
            map.SetZone(10, y, ZoneType.Residential);
            map.SetBuildingFootprint(home, new(10, y, 1, 1));
            map.SetHousehold(10, y, new Household(3, 2, 0));
        }
        game.Touch();
        int remote = map.Index(10, 5), roadside = map.Index(10, 19);
        Assert.Equal(0, game.Network.AccessRank(remote));
        Assert.True(game.Network.AccessRank(roadside) > 0);
        var penalties = new Dictionary<string, double>();
        var far = CityAnalysis.Feel(game, remote, 10, 30, 0, 0, 0, penalties, 1);
        var near = CityAnalysis.Feel(game, roadside, 10, 30, 0, 0, 0, null, 1);
        Assert.Equal(near.Happiness, far.Happiness);
        Assert.DoesNotContain("Slow journeys", penalties.Keys);
        Assert.DoesNotContain(game.Indicators.Complaints, c => c.Reason is "Slow journeys" or "Road access");
    }
}
