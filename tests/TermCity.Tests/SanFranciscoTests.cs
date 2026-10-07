using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class SanFranciscoTests
{
    [Fact]
    public void StartingLoadingAndRestartingSfDoesNotShowTheFirstCityGuide()
    {
        string path = Path.Combine(Path.GetTempPath(), $"termcity-sf-guide-{Guid.NewGuid():N}.json");
        try
        {
            var sf = City();
            var session = new GameSession(sf, path, showGuide: true);
            Assert.True(sf.Paused);
            Assert.False(session.GuideVisible);
            Assert.Null(session.Prompt);
            session.RequestNewCity(restart: true);
            session.SelectPrompt(1); // Discard the unsaved initial city if guarded.
            Assert.True(session.Game.Config.SanFrancisco);
            Assert.True(session.Game.Paused);
            Assert.False(session.GuideVisible);
            Assert.Null(session.Prompt);

            SaveGameStore.Save(sf, path);
            var normal = new GameSession(TestCity.Flat(), path, showGuide: true);
            Assert.True(normal.GuideVisible);
            Assert.Null(normal.Prompt);
            Assert.True(normal.LoadFrom(path));
            Assert.False(normal.GuideVisible);
            Assert.Null(normal.Prompt);
            normal.NewGame(new GameConfig());
            Assert.True(normal.GuideVisible);
            Assert.Null(normal.Prompt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CityGame City(int seed = 42) => CityGame.New(
        GodotOptions.Parse(["--size", "SF", "--seed", seed.ToString()]).Config);

    private static Pos At(GameMap map, int x, int y) =>
        new(x * (map.Width - 1) / 100, y * (map.Height - 1) / 100);

    [Theory]
    [InlineData("SF")]
    [InlineData("sf")]
    [InlineData(" Sf ")]
    public void PresetUsesLargeDimensionsAndBothParsersRecognizeIt(string name)
    {
        Assert.True(MapSize.TryParse(name, out var size, out _));
        Assert.Equal(640, size.Width);
        Assert.Equal(384, size.Height);
        Assert.True(size.SanFrancisco);
        Assert.True(GodotOptions.Parse(["--size", name]).Config.SanFrancisco);
        Assert.False(GodotOptions.Parse(["--size", "SF", "--size", "medium"]).Config.SanFrancisco);
        Assert.False(GodotOptions.Parse(["--size", "SF", "--size", "320x192"]).Config.SanFrancisco);
    }

    [Fact]
    public void NewScenarioNormalizesDimensionsToLarge()
    {
        var game = CityGame.New(new GameConfig { SanFrancisco = true });
        Assert.Equal(640, game.Map.Width);
        Assert.Equal(384, game.Map.Height);
    }

    [Fact]
    public void CoastHillsIslandsAndParkHaveFixedGeography()
    {
        var game = City();
        var map = game.Map;
        Assert.Equal("San Francisco", game.CityName);
        Assert.Equal(640, map.Width);
        Assert.Equal(384, map.Height);
        foreach (var (x, y) in new[] { (10, 60), (70, 70), (52, 33), (52, 39), (28, 90) })
        {
            var p = At(map, x, y);
            Assert.Equal(DefaultTerrains.WaterName, map.TerrainAt(p.X, p.Y).Name);
        }

        foreach (var (x, y) in new[] { (52, 36), (59, 29), (72, 50), (71, 53) })
        {
            var p = At(map, x, y);
            Assert.True(map.TerrainAt(p.X, p.Y).Buildable, $"Expected island at {x},{y}.");
        }
        foreach (var (x, y) in new[] { (43, 77), (42, 86), (50, 54), (49, 48), (55, 48) })
        {
            var p = At(map, x, y);
            Assert.Equal(DefaultTerrains.HillName, map.TerrainAt(p.X, p.Y).Name);
        }
        var park = At(map, 36, 66);
        Assert.Equal(ZoneType.None, map.ZoneAt(park.X, park.Y));
        Assert.True(map.FeatureAt(park.X, park.Y) is not null || map.HasRoad(park.X, park.Y));
        var strait = At(map, 32, 34);
        Assert.True(map.HasRoad(strait.X, strait.Y));
        Assert.False(map.TerrainAt(strait.X, strait.Y).Buildable);
        Assert.True(game.Network.IsConnected(map, strait.X, strait.Y));
    }

    [Fact]
    public void BuiltDistrictsMatchCityAreasAndNeverPlaceBuildingsOnWaterOrRoads()
    {
        var game = City();
        var map = game.Map;
        AssertRegion(54, 51, ZoneType.Commercial); // Financial District
        AssertRegion(53, 69, ZoneType.Commercial); // SoMa
        AssertRegion(60, 80, ZoneType.Industrial); // Dogpatch
        AssertRegion(61, 90, ZoneType.Industrial); // Hunters Point / southeast
        AssertRegion(30, 73, ZoneType.Residential); // Sunset
        AssertRegion(47, 80, ZoneType.Residential); // Mission
        AssertRegion(43, 23, ZoneType.Commercial); // Sausalito
        AssertRegion(52, 13, ZoneType.Residential); // Marin / Tiburon
        AssertRegion(91, 31, ZoneType.Commercial); // Berkeley
        AssertRegion(92, 41, ZoneType.Residential); // East Bay neighborhoods
        AssertRegion(92, 60, ZoneType.Commercial); // Downtown Oakland
        AssertRegion(87, 64, ZoneType.Industrial); // Oakland port
        AssertRegion(80, 76, ZoneType.Residential); // Alameda
        foreach (var zone in Zones.Placeable)
        {
            Assert.True(game.Stats.For(zone).Filled > 100);
            Assert.True(game.Stats.For(zone).Served > game.Stats.For(zone).Zoned * 0.8);
        }
        Assert.True(game.Stats.Population > 1_000);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                _ = CellRenderer.Render(game, x, y);
                if (map.BuildingAt(x, y) is null) continue;
                Assert.True(map.TerrainAt(x, y).Buildable);
                Assert.False(map.HasRoad(x, y));
                if (!map.BuildingAt(x, y)!.IsService) Assert.NotEqual(ZoneType.None, map.ZoneAt(x, y));
            }
        }

        void AssertRegion(int x, int y, ZoneType zone)
        {
            var center = At(map, x, y);
            Assert.Contains(new CellRect(center.X - 2, center.Y - 2, 5, 5).Cells(),
                p => map.ZoneAt(p.X, p.Y) == zone && map.BuildingAt(p.X, p.Y) is not null &&
                    game.Network.IsServed(map, p.X, p.Y));
        }
    }

    [Fact]
    public void GeographyAndDistrictsAreRepeatableAndSavesAndRestartsPreserveTheScenario()
    {
        var game = City();
        var again = City();
        var differentSeed = City(7);
        Assert.Equal(game.Map.TerrainLayer, differentSeed.Map.TerrainLayer);
        Assert.Equal(game.Map.ZoneLayer, differentSeed.Map.ZoneLayer);
        Assert.Equal(game.Map.BuildingLayer, differentSeed.Map.BuildingLayer);
        Assert.Equal(game.Map.HouseholdLayer, again.Map.HouseholdLayer);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.True(loaded.Config.SanFrancisco);
        Assert.Equal(game.CityName, loaded.CityName);
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(game.Map.RoadLayer, loaded.Map.RoadLayer);
        var session = new GameSession(game);
        session.NewGame(loaded.Config);
        Assert.True(session.Game.Config.SanFrancisco);
        Assert.Equal("San Francisco", session.Game.CityName);
        Assert.Equal(game.Map.ZoneLayer, session.Game.Map.ZoneLayer);
    }
}
