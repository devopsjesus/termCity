using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class MapVisualInvalidationTests
{
    [Fact]
    public void ServiceAgeAndClockUpdatesDoNotInvalidateUnchangedMapArtwork()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        foreach (int road in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(road % game.Map.Width, road / game.Map.Width, false);
        game.Touch();
        var well = game.Map.Content.Buildings.Get("Town Well");
        var result = game.PlaceBuilding(well, well.FootprintAt(10, 10));
        Assert.True(result.Success, result.Message);
        int map = game.MapVersion, analysis = game.AnalysisVersion;
        game.Paused = false;
        for (int step = 0; step < 100; step++) game.Update(.1);
        Assert.True(game.Week > 0);
        Assert.Equal(map, game.MapVersion);
        Assert.True(game.AnalysisVersion > analysis);
    }

    [Fact]
    public void BuildingAndTerrainChangesStillInvalidateNavigation()
    {
        var game = TestCity.Flat();
        int navigation = game.Map.NavigationVersion;
        game.Map.SetBuilding(10, 10, game.Map.Content.Buildings.Get("Cottage"));
        Assert.True(game.Map.NavigationVersion > navigation);
        navigation = game.Map.NavigationVersion;
        game.Map.SetTerrain(11, 10, game.Map.Content.Terrains.Get("Water"));
        Assert.True(game.Map.NavigationVersion > navigation);
    }
}
