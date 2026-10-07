using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class PersistenceTests
{
    [Fact]
    public void OldSavesHaveAFallbackNameAndInvalidNamesAreRejected()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json.AsObject().Remove("CityName");
        Assert.Equal("New City", SaveGameStore.Deserialize(json.ToJsonString()).CityName);
        json["CityName"] = new string('X', 17);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void StartingYearIsPreservedAndOldSavesKeepTheirCalendar()
    {
        var game = TestCity.Flat(config: new GameConfig { StartingYear = 2040 });
        TestCity.Advance(game, 52);
        string json = SaveGameStore.Serialize(game);
        Assert.Equal(2041, SaveGameStore.Deserialize(json).Year);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        legacy["Config"]!.AsObject().Remove("StartingYear");
        Assert.Equal(2, SaveGameStore.Deserialize(legacy.ToJsonString()).Year);
    }

    [Fact]
    public void SaveAndLoadRoundTripsTheWholeGame()
    {
        var game = TestCity.Flat(seed: 77);
        var content = game.Map.Content;
        game.Map.SetTerrain(5, 5, content.Terrains.Get("Hill"));
        game.Map.SetTerrain(6, 5, content.Terrains.Get("Water"));
        game.Map.SetFeature(7, 5, content.Features.Get("Rock"));
        game.Designate(new CellRect(60, 21, 12, 1), ZoneType.Residential);
        game.Designate(new CellRect(60, 19, 3, 1), ZoneType.Commercial);
        game.Speed = GameSpeed.Fast;
        game.Taxes.Industrial = 0.07;
        game.Update(0.2);
        TestCity.Advance(game, 6);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));

        Assert.Equal(game.Money, loaded.Money);
        Assert.Equal(game.Week, loaded.Week);
        Assert.Equal(game.Speed, loaded.Speed);
        Assert.Equal(game.Config, loaded.Config);
        Assert.Equal(0.07, loaded.Taxes.Industrial);
        Assert.Equal(game.Map.TerrainLayer, loaded.Map.TerrainLayer);
        Assert.Equal(game.Map.FeatureLayer, loaded.Map.FeatureLayer);
        Assert.Equal(game.Map.BuildingLayer, loaded.Map.BuildingLayer);
        Assert.Equal(game.Map.RoadLayer, loaded.Map.RoadLayer);
        Assert.Equal(game.Map.ZoneLayer, loaded.Map.ZoneLayer);
        Assert.Equal(game.Map.HouseholdLayer, loaded.Map.HouseholdLayer);
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.True(game.Stats.Population > 0);
    }

    [Fact]
    public void LoadedGameContinuesIdenticallyToTheOriginal()
    {
        var game = TestCity.Flat(seed: 5);
        game.Designate(new CellRect(0, 21, 30, 1), ZoneType.Residential);
        TestCity.Advance(game, 3);

        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        TestCity.Advance(game, 8);
        TestCity.Advance(loaded, 8);

        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(game.Money, loaded.Money);
        Assert.Equal(game.Map.HouseholdLayer, loaded.Map.HouseholdLayer);
    }

    [Fact]
    public void SaveToDiskAndLoadBack()
    {
        string path = Path.Combine(Path.GetTempPath(), "termcity-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var game = CityGame.New(new GameConfig { Seed = 3 });
            SaveGameStore.Save(game, path);
            Assert.Equal(game.Map.TerrainLayer, SaveGameStore.Load(path).Map.TerrainLayer);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"Version\": 99}")]
    public void CorruptSavesThrowInvalidData(string json)
    {
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json));
    }
}
