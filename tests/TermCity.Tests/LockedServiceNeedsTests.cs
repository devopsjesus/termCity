using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class LockedServiceNeedsTests
{
    [Theory]
    [InlineData(ServiceKind.Fire, 250)]
    [InlineData(ServiceKind.Police, 250)]
    [InlineData(ServiceKind.Health, 250)]
    [InlineData(ServiceKind.Education, 250)]
    [InlineData(ServiceKind.Recreation, 500)]
    public void ExpectationsStartAtTheFirstAvailableBuildingAndRemainAfterPopulationFalls(ServiceKind kind, int threshold)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.Equal(threshold, CityProgression.ServiceUnlockPopulation(game, kind));
        Assert.Equal(0, CityAnalysis.Need(game, kind, threshold - 1));
        Assert.True(CityAnalysis.Need(game, kind, threshold) > 0);
        game.HighestMilestone = threshold;
        Assert.True(CityAnalysis.Need(game, kind, threshold - 1) > 0);
    }

    [Fact]
    public void LockedCareDoesNotReduceHomeHappinessOrAppearInPetitions()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var map = game.Map;
        for (int x = 10; x < 20; x++)
        {
            map.SetZone(x, 19, ZoneType.Residential);
            map.SetBuilding(x, 19, map.Content.Buildings.Get("Cottage"));
            map.SetHousehold(x, 19, new Household(8, 0, 0));
        }
        map.SetBuildingFootprint(map.Content.Buildings.Get("Woodlot"), new(25, 18, 2, 2));
        map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(28, 19, 1, 1));
        game.Touch();
        int index = map.Index(10, 19);
        var penalties = new Dictionary<string, double>();
        CityAnalysis.Feel(game, index, game.Stats.Population, 30, 0, 0, 0, penalties, 1);
        Assert.DoesNotContain("Lawlessness", penalties.Keys);
        Assert.DoesNotContain("Fire Watch", penalties.Keys);
        Assert.DoesNotContain("Physic", penalties.Keys);
        Assert.DoesNotContain("Learning", penalties.Keys);
        Assert.DoesNotContain("Commons", penalties.Keys);
        for (int i = 0; i < game.Indicators.Complaints.Count; i++)
        {
            var petition = PeasantNeeds.Describe(game, 0, i);
            Assert.DoesNotContain(petition?.Reason ?? "", new[] { "Lawlessness", "Fire Watch", "Physic", "Learning", "Commons" });
        }
        var report = BuildingReport.Build(game, new Pos(10, 19));
        Assert.Contains(report.Groups[0].Rows, row => row[0].Text == CityReport.ServiceName(ServiceKind.Health) &&
            row[1].Text.Contains("Locked") && row[1].Text.Contains("not expected"));
        game.HighestMilestone = 250;
        game.Touch();
        Assert.Contains(game.Indicators.Complaints, c => c.Reason is "Physic" or "Learning" or "Fire Watch");
        Assert.DoesNotContain(game.Indicators.Complaints, c => c.Reason == "Commons");
        Assert.Contains(Enumerable.Range(0, game.Indicators.Complaints.Count)
            .Select(i => PeasantNeeds.Describe(game, 0, i)!.Text), text => text.Contains("now available"));
    }
}
