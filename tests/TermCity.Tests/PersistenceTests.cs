using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Terrain;

namespace TermCity.Tests;

public class PersistenceTests
{
    [Fact]
    public void MissingAndInvalidNamesAreRejected()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json.AsObject().Remove("CityName");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        json["CityName"] = new string('X', 17);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void StartingYearIsPreservedAndRequired()
    {
        var game = TestCity.Flat(config: new GameConfig { StartingYear = 2040 });
        TestCity.Advance(game, 52);
        string json = SaveGameStore.Serialize(game);
        Assert.Equal(2041, SaveGameStore.Deserialize(json).Year);
        var incomplete = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        incomplete["Config"]!.AsObject().Remove("StartingYear");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(incomplete.ToJsonString()));
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
    public void MidweekSavePreservesTheWeeklyReport()
    {
        var game = TestCity.Flat(seed: 5);
        Assert.True(game.Designate(new CellRect(0, 21, 30, 1), ZoneType.Residential).Success);
        for (int day = 0; day < 3; day++) game.AdvanceDay();
        Assert.True(game.Stats.Population > 0);

        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        game.AdvanceWeek();
        loaded.AdvanceWeek();

        Assert.Equal(game.Map.HouseholdLayer, loaded.Map.HouseholdLayer);
        Assert.Equal(game.LastReport, loaded.LastReport);
    }

    [Fact]
    public void FullRulesVacanciesKeepTheirRandomOrderAfterLoading()
    {
        var game = TestCity.Flat(seed: 5, config: new GameConfig { StartingMoney = 1_000_000 }, rules: CityRules.Full);
        foreach (int x in new[] { 50, 40, 30, 20, 10 })
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
            game.Map.SetHousehold(x, 21, new Household(1, 0, 0));
        }
        game.Touch();
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new CellRect(80, 18, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new CellRect(85, 18, 1, 1)).Success);
        game.GrowthState = new(true, 7, 0, 0, 0, 0, 0);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));

        for (int day = 0; day < 6; day++)
        {
            game.AdvanceDay();
            loaded.AdvanceDay();
            Assert.True(game.Stats.Population > 5);
            Assert.Equal(game.Rng.State, loaded.Rng.State);
            Assert.Equal(game.Map.HouseholdLayer, loaded.Map.HouseholdLayer);
        }
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
    [InlineData("Tally")]
    [InlineData("Growth")]
    [InlineData("Taxes")]
    [InlineData("Funding")]
    [InlineData("GrainWeeks")]
    [InlineData("HarvestQuality")]
    [InlineData("Hunger")]
    [InlineData("HighestRank")]
    [InlineData("TributeArrears")]
    [InlineData("BuildingFootprints")]
    [InlineData("ZoneRemovals")]
    public void CurrentSaveStateIsRequired(string property)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json.AsObject().Remove(property);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Theory]
    [InlineData("Config")]
    [InlineData("CityName")]
    [InlineData("Tally")]
    [InlineData("Growth")]
    [InlineData("Taxes")]
    [InlineData("Funding")]
    [InlineData("BuildingFootprints")]
    [InlineData("ZoneRemovals")]
    public void NullCityStateIsRejected(string property)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json[property] = null;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Theory]
    [InlineData("Terrain", "Grass")]
    [InlineData("Features", "Missing Feature")]
    [InlineData("Buildings", "House")]
    [InlineData("RoadTypes", "Street")]
    public void UnknownOrRenamedPaletteTypesAreRejected(string layer, string name)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json[layer]!["Palette"]![0] = name;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void InvalidCalendarAndConfigurationAreRejectedWithoutClamping()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json["Day"] = 7;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        json["Day"] = 0;
        json["Config"]!["DaysPerWeek"] = 0;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void EveryTerrainByteIdRoundTripsIncluding255()
    {
        var terrains = new TerrainRegistry();
        for (int i = 0; i <= byte.MaxValue; i++)
            terrains.Register(new TerrainType
            {
                Name = $"Terrain {i}", Glyphs = ["."], Foreground = Rgb.Hex(0xffffff), Background = Rgb.Hex(0),
            });
        var content = new GameContent { Terrains = terrains };
        var map = new GameMap(16, 16, content);
        for (int i = 0; i <= byte.MaxValue; i++)
            map.SetTerrain(i % 16, i / 16, terrains[(byte)i]);
        var game = new CityGame(new GameConfig { MapWidth = 16, MapHeight = 16, StartingYear = 2026 }, map, new GameRandom(1));

        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game), content);

        Assert.Equal(game.Map.TerrainLayer, loaded.Map.TerrainLayer);
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
