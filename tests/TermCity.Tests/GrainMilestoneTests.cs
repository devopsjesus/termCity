using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class GrainMilestoneTests
{
    [Theory]
    [InlineData("Market Cross")]
    [InlineData("Granary")]
    public void GrainBuildingsUnlockAtVillage(string name)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var type = game.Map.Content.Buildings.Get(name);
        Assert.Equal(500, CityProgression.RequiredPopulation(type));
        game.HighestMilestone = 100;
        Assert.False(game.PlaceBuilding(type, new(30, 19, 1, 1)).Success);
        Assert.Contains("500", game.BuildingPlacementError(type, 30, 19));
        game.HighestMilestone = 500;
        Assert.True(game.PlaceBuilding(type, new(30, 19, 1, 1)).Success);
    }

    [Fact]
    public void EarlySettlementHasNoGrainCostHungerOrFamine()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 19, ZoneType.Residential);
        game.Map.SetBuilding(10, 19, game.Map.Content.Buildings.Get("Cottage"));
        game.Map.SetHousehold(10, 19, new Household(40, 20, 0));
        game.GrainWeeks = 0;
        game.Hunger = 1;
        game.Touch();
        int money = game.Money;
        for (int week = 0; week < 52; week++) Harvest.RunWeek(game, new WeekTally());
        Assert.False(game.GrainRequired);
        Assert.Equal(0, game.Hunger);
        Assert.Equal(0, Harvest.NeedPerWeek(game));
        Assert.Equal(money, game.Money);
        Assert.DoesNotContain(game.Events, e => e.Kind is EventKind.Famine or EventKind.Harvest);
        var grain = SidebarReport.CityRows(game).Single(row => row[0].Text == "Grain")[1];
        Assert.Contains("Village", grain.Text);
        Assert.Equal(SidebarReport.NoneColor, grain.Color);
        Assert.Contains("not expected", SidebarReport.ServiceStatus(game, ServiceKind.Granary).Text);
    }

    [Fact]
    public void GrainRemainsActiveAfterReachingVillageAndSurvivesSaveLoad()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 19, ZoneType.Residential);
        game.Map.SetBuilding(10, 19, game.Map.Content.Buildings.Get("Cottage"));
        game.Map.SetHousehold(10, 19, new Household(20, 0, 0));
        game.HighestMilestone = 500;
        game.GrainWeeks = 0;
        game.Money = 0;
        game.Touch();
        Harvest.RunWeek(game, new WeekTally());
        Assert.True(game.GrainRequired);
        Assert.True(game.Hunger > 0);
        Assert.Contains(game.Events, e => e.Kind == EventKind.Famine);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.True(loaded.GrainRequired);
        Assert.Equal(game.Hunger, loaded.Hunger);
    }
}
