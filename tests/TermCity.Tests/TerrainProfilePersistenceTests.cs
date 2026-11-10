using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class TerrainProfilePersistenceTests
{
    [Fact]
    public void NewRandomMapsUseTripleHillsAndRoundTripTheProfile()
    {
        var game = CityGame.New(new GameConfig { MapWidth = 160, MapHeight = 160, Seed = 42 });
        Assert.Equal(TerrainElevationProfile.Triple, game.Map.ElevationProfile);
        Assert.Equal(4.05f, TerrainSprite.MaximumHillFor(game.Map), precision: 4);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(TerrainElevationProfile.Triple, loaded.Map.ElevationProfile);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(loaded));
    }

    [Fact]
    public void SavesWithoutAProfileRetainOriginalHeights()
    {
        var game = CityGame.New(new GameConfig { MapWidth = 160, MapHeight = 160, Seed = 42 });
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        json.Remove("ElevationProfile");
        var loaded = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.Equal(TerrainElevationProfile.Current, loaded.Map.ElevationProfile);
        Assert.Equal(1.35f, TerrainSprite.MaximumHillFor(loaded.Map), precision: 4);
        json["ElevationProfile"] = "Invalid";
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void PopulatedScenariosRetainTheirOriginalTerrainScale()
    {
        var game = CityGame.New(new GameConfig { Scenario = CityScenario.SanFrancisco, Seed = 42 });
        Assert.Equal(TerrainElevationProfile.Current, game.Map.ElevationProfile);
    }
}
