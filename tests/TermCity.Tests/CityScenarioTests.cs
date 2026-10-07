using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class CityScenarioTests
{
    [Theory]
    [InlineData("nap", CityScenario.Naples, "Naples")]
    [InlineData("GEN", CityScenario.Genoa, "Genoa")]
    [InlineData(" Lub ", CityScenario.Lubeck, "Lubeck")]
    [InlineData("YRK", CityScenario.York, "York")]
    public void CityPresetsUseLargeDimensionsAndPersistTheirPopulatedLayout(
        string preset, CityScenario scenario, string name)
    {
        Assert.True(MapSize.TryParse(preset, out var size, out _));
        Assert.Equal(640, size.Width);
        Assert.Equal(384, size.Height);
        Assert.Equal(scenario, size.Scenario);
        var config = GodotOptions.Parse(["--size", preset, "--seed", "42"]).Config;
        Assert.Equal(scenario, config.Scenario);
        var game = CityGame.New(config);
        Assert.Equal(name, game.CityName);
        Assert.Equal(640, game.Map.Width);
        Assert.Equal(384, game.Map.Height);
        Assert.True(game.Stats.Population > 1_000);
        foreach (var zone in Zones.Placeable)
        {
            Assert.True(game.Stats.For(zone).Filled > 100);
            Assert.True(game.Stats.For(zone).Served > game.Stats.For(zone).Zoned * 0.8,
                $"{name} {zone} should have road access.");
        }
        for (int i = 0; i < game.Map.BuildingLayer.Length; i++)
        {
            if (game.Map.BuildingLayer[i] == 0) continue;
            Assert.False(game.Map.RoadLayer[i]);
            Assert.True(game.Map.Content.Terrains[game.Map.TerrainLayer[i]].Buildable);
            if (!game.Map.Content.Buildings[game.Map.BuildingLayer[i]].IsService) Assert.NotEqual(ZoneType.None, game.Map.ZoneLayer[i]);
        }
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(scenario, loaded.Config.Scenario);
        Assert.Equal(game.Config, loaded.Config);
        Assert.Equal(name, loaded.CityName);
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(game.Map.TerrainLayer, loaded.Map.TerrainLayer);
        Assert.Equal(game.Map.ZoneLayer, loaded.Map.ZoneLayer);
        Assert.Equal(game.Map.BuildingLayer, loaded.Map.BuildingLayer);
        var session = new GameSession(game, showGuide: true);
        Assert.False(session.GuideVisible);
        Assert.Null(session.Prompt);
        session.NewGame(loaded.Config);
        Assert.Equal(scenario, session.Game.Config.Scenario);
        Assert.Equal(game.Map.ZoneLayer, session.Game.Map.ZoneLayer);
        Assert.Equal(game.Map.HouseholdLayer, session.Game.Map.HouseholdLayer);
        Assert.False(session.GuideVisible);
        Assert.Null(session.Prompt);
        Assert.Equal(CityScenario.Random,
            GodotOptions.Parse(["--size", preset, "--size", "large"]).Config.Scenario);
    }

    [Theory]
    [InlineData("NAP", 10, 80, "Water")]
    [InlineData("NAP", 68, 10, "Hill")]
    [InlineData("GEN", 10, 50, "Water")]
    [InlineData("GEN", 39, 69, "Water")]
    [InlineData("GEN", 34, 72, "Meadow")] // Coronado
    [InlineData("GEN", 75, 32, "Hill")]
    [InlineData("LUB", 90, 50, "Water")]
    [InlineData("LUB", 50, 40, "Meadow")]
    [InlineData("YRK", 70, 50, "Water")] // Mississippi
    [InlineData("YRK", 50, 20, "Water")] // Missouri
    [InlineData("YRK", 20, 47, "Hill")]
    public void GeographyMatchesTheCitysDefiningLandmarks(string preset, int x, int y, string terrain)
    {
        var game = City(preset);
        var p = At(game.Map, x, y);
        Assert.Equal(terrain, game.Map.TerrainAt(p.X, p.Y).Name);
    }

    [Theory]
    [InlineData("NAP", 58, 54, ZoneType.Commercial)] // Downtown
    [InlineData("NAP", 31, 53, ZoneType.Commercial)] // Santa Monica
    [InlineData("NAP", 68, 69, ZoneType.Industrial)] // Vernon
    [InlineData("NAP", 73, 92, ZoneType.Industrial)] // Long Beach
    [InlineData("NAP", 44, 53, ZoneType.Residential)] // Westside
    [InlineData("GEN", 52, 64, ZoneType.Commercial)] // Downtown
    [InlineData("GEN", 55, 83, ZoneType.Industrial)] // National City
    [InlineData("GEN", 61, 44, ZoneType.Residential)] // Inland neighborhoods
    [InlineData("LUB", 65, 55, ZoneType.Commercial)] // Loop
    [InlineData("LUB", 45, 79, ZoneType.Industrial)] // South Side
    [InlineData("LUB", 54, 32, ZoneType.Residential)] // North Side
    [InlineData("YRK", 61, 54, ZoneType.Commercial)] // Downtown
    [InlineData("YRK", 75, 38, ZoneType.Industrial)] // Metro East riverfront
    [InlineData("YRK", 46, 62, ZoneType.Residential)] // South City
    public void RepresentativeDistrictsHaveOccupiedRoadServedLots(string preset, int x, int y, ZoneType zone)
    {
        var game = City(preset);
        var center = At(game.Map, x, y);
        Assert.Contains(new CellRect(center.X - 3, center.Y - 3, 7, 7).Cells(),
            p => game.Map.ZoneAt(p.X, p.Y) == zone && game.Map.BuildingAt(p.X, p.Y) is not null &&
                game.Network.IsServed(game.Map, p.X, p.Y));
    }

    [Fact]
    public void LegacySfFlagStillLoadsWithoutScenarioMetadata()
    {
        var game = City("CON");
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json["Config"]!.AsObject().Remove("Scenario");
        var loaded = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.Equal(CityScenario.Constantinople, loaded.Config.Scenario);
        Assert.Equal(game.Map.ZoneLayer, loaded.Map.ZoneLayer);
    }

    private static CityGame City(string preset) => CityGame.New(
        GodotOptions.Parse(["--size", preset, "--seed", "42"]).Config);

    private static Pos At(GameMap map, int x, int y) =>
        new(x * (map.Width - 1) / 100, y * (map.Height - 1) / 100);
}

public class ScenarioSeedingTests
{
    [Theory]
    [InlineData(CityScenario.Constantinople)]
    [InlineData(CityScenario.Naples)]
    [InlineData(CityScenario.York)]
    public void ScenarioCitiesStartPoweredWateredAndServed(CityScenario scenario)
    {
        var game = CityGame.New(new GameConfig { Scenario = scenario, MapWidth = 640, MapHeight = 384 });
        Assert.True(game.Services.Power.Ratio >= 0.99, $"power {game.Services.Power.Ratio}");
        Assert.True(game.Services.Water.Ratio >= 0.99, $"water {game.Services.Water.Ratio}");
        foreach (var kind in TermCity.Core.Buildings.ServiceKinds.Area)
        {
            Assert.True(game.Indicators.CoverageOf(kind) > 15, $"{kind} cover {game.Indicators.CoverageOf(kind)}");
        }

        Assert.True(game.Money >= 8 * game.Finance.Expenses);
        TestCity.Advance(game, 20);
        Assert.True(game.Money > 0);
    }
}
