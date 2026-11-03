using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ResidentialRoadPreferenceTests
{
    private static void Arrive(CityGame game)
    {
        game.Day = game.Config.DaysPerWeek - 1;
        game.GrowthState = new(true, 1, 0, 0, 0, 0, 0);
        game.AdvanceDay();
    }

    [Theory]
    [InlineData(CityRules.Classic, 1)]
    [InlineData(CityRules.Classic, 42)]
    [InlineData(CityRules.Full, 1)]
    [InlineData(CityRules.Full, 42)]
    public void NewHomesUseRoadsidePlotsBeforeDistantDesignatedLand(CityRules rules, int seed)
    {
        var game = TestCity.Flat(seed: seed, rules: rules);
        game.Designate(new(10, 0, 20, 10), ZoneType.Residential);
        game.Designate(new(10, 15, 20, 5), ZoneType.Residential);
        Arrive(game);
        var home = Assert.Single(game.Map.BuildingFootprints.Values);
        Assert.Equal(15, home.Y);
        Assert.Equal(5, home.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void FamiliesPreferExistingVacantHomesWithShorterRoadApproaches(int seed)
    {
        var game = TestCity.Flat(seed: seed, rules: CityRules.Full);
        var cottage = game.Map.Content.Buildings.Get("Cottage");
        foreach (var p in new[] { new Pos(10, 0), new Pos(10, 15) })
            TestCity.Stamp(game, cottage.Name, p);
        foreach (var (name, x) in new[] { ("Woodlot", 20), ("Town Well", 32) })
        {
            var service = game.Map.Content.Buildings.Get(name);
            game.Map.SetBuildingFootprint(service, service.FootprintAt(x, 5));
        }
        game.Touch();
        game.Day = game.Config.DaysPerWeek - 1;
        Assert.True(CityAnalysis.Operating(game, game.Map.Index(10, 15)));
        Assert.True(CityAnalysis.Operating(game, game.Map.Index(10, 0)));
        Arrive(game);
        Assert.True(game.Map.HouseholdLayer[game.Map.Index(10, 15)].Total > 0);
        Assert.True(game.Map.HouseholdLayer[game.Map.Index(10, 0)].IsEmpty);
    }

    [Fact]
    public void InvalidRoadsidePlotsDoNotBlockAValidMoreDistantHome()
    {
        var game = TestCity.Flat();
        game.Map.SetZone(10, 19, ZoneType.Residential);
        game.Map.SetTerrain(10, 19, game.Map.Content.Terrains.Get("Water"));
        game.Designate(new(15, 15, 5, 5), ZoneType.Residential);
        Arrive(game);
        Assert.Equal(new CellRect(15, 15, 5, 5), Assert.Single(game.Map.BuildingFootprints.Values));
    }

    [Fact]
    public void HomesStillSettleWhenNoRoadsExist()
    {
        var game = TestCity.Flat();
        foreach (int cell in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(cell % game.Map.Width, cell / game.Map.Width, false);
        game.Designate(new(10, 10, 5, 5), ZoneType.Residential);
        for (int attempt = 0; attempt < 32 && game.Map.BuildingFootprints.Count == 0; attempt++) Arrive(game);
        Assert.Equal(new CellRect(10, 10, 5, 5), Assert.Single(game.Map.BuildingFootprints.Values));
    }

    [Fact]
    public void DisconnectedLocalRoadsStillAttractSettlement()
    {
        var game = TestCity.Flat();
        foreach (int cell in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(cell % game.Map.Width, cell / game.Map.Width, false);
        for (int x = 10; x <= 20; x++) game.Map.SetRoad(x, 20, true);
        game.Designate(new(10, 5, 10, 5), ZoneType.Residential);
        game.Designate(new(10, 15, 10, 5), ZoneType.Residential);
        Arrive(game);
        Assert.Equal(15, Assert.Single(game.Map.BuildingFootprints.Values).Y);
    }

    [Fact]
    public void RoadsideSelectionRemainsDeterministicAfterReload()
    {
        var game = TestCity.Flat(seed: 42);
        game.Designate(new(10, 10, 20, 10), ZoneType.Residential);
        Arrive(game);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        for (int day = 0; day < 5; day++)
        {
            Arrive(game);
            Arrive(restored);
        }
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(restored));
    }
}
