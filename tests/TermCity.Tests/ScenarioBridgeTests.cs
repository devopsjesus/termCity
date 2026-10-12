using TermCity.Core.Simulation;
using TermCity.Core.Rendering;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ScenarioBridgeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(99)]
    [InlineData(1981019679)]
    public void RandomGeneratedBridgesDoNotHaveWaterDeadEnds(int seed) =>
        VerifyGeneratedBridges(seed, 160, 96);

    [Theory]
    [InlineData(42, 320, 192)]
    [InlineData(1773051603, 640, 384)]
    public void CurrentInterfaceMapSizesDoNotGenerateWaterDeadEnds(int seed, int width, int height) =>
        VerifyGeneratedBridges(seed, width, height);

    private static void VerifyGeneratedBridges(int seed, int width, int height)
    {
        var game = CityGame.New(new GameConfig { Seed = seed, MapWidth = width, MapHeight = height });
        var map = game.Map;
        foreach (int index in map.RoadCells)
        {
            int x = index % map.Width, y = index / map.Width;
            if (map.TerrainAt(x, y).Buildable) continue;
            int connections = RoadNetwork.Neighbors.Count(direction => map.HasRoad(x + direction.Dx, y + direction.Dy));
            Assert.Equal(2, connections);
        }
        var roads = new RoadVectorLayer();
        roads.Sync(game);
        var layout = new RoadMeshLayout(map, roads.Paths);
        foreach (var path in roads.Paths.Where(path => !path.Closed))
        {
            foreach (var endpoint in new[] { 0, path.Count - 1 })
            {
                float x = (float)path.Xs[endpoint] / RoadCurves.CellWidth;
                float y = (float)path.Ys[endpoint] / RoadCurves.CellHeight;
                var at = layout.RoadAt(new(x, y));
                if (at?.Bridge != true) continue;
                Assert.True(map.TerrainAt((int)MathF.Floor(x), (int)MathF.Floor(y)).Buildable,
                    $"Seed {seed}: {path.Type.Name} bridge endpoint ({x},{y}) is in visible water.");
            }
        }
    }

    private static GameMap River()
    {
        var map = new GameMap(16, 16, new GameContent());
        var water = map.Content.Terrains.Get(DefaultTerrains.WaterName);
        for (int y = 0; y < map.Height; y++)
            for (int x = 7; x <= 8; x++) map.SetTerrain(x, y, water);
        return map;
    }

    [Fact]
    public void BlockedFarBankDoesNotLeaveHalfOfAGeneratedBridge()
    {
        var map = River();
        for (int y = 0; y <= 6; y++) map.SetRoad(9, y, true);
        CityMapGeometry.Road(map, map.Content.Roads.Default, (0, 50), (100, 50));
        foreach (int index in map.RoadCells.Where(index => !map.TerrainAt(index % map.Width, index / map.Width).Buildable))
        {
            int x = index % map.Width, y = index / map.Width;
            Assert.Equal(2, RoadNetwork.Neighbors.Count(direction => map.HasRoad(x + direction.Dx, y + direction.Dy)));
        }
    }

    [Fact]
    public void FallbackHighwaysDoNotRunOutIntoOpenWater()
    {
        var map = new GameMap(16, 16, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < 8; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        HighwayGenerator.Generate(new GenerationContext(map, 42));
        Assert.NotEmpty(map.RoadCells);
        Assert.All(map.RoadCells, index => Assert.True(map.TerrainAt(index % map.Width, index / map.Width).Buildable));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(50, 100)]
    public void RouteEndpointsInWaterDoNotCreateRiverSpurs(int start, int end)
    {
        var map = River();
        CityMapGeometry.Road(map, map.Content.Roads.Default, (start, 50), (end, 50));
        Assert.NotEmpty(map.RoadCells);
        Assert.All(map.RoadCells, index => Assert.True(map.TerrainAt(index % map.Width, index / map.Width).Buildable));
    }

    [Fact]
    public void StreetGridStaysOnLandAndDoesNotAddBridgeJunctions()
    {
        var map = River();
        var roads = map.Content.Roads.OrderBy(type => type.Rank).ToArray();
        CityMapGeometry.Road(map, roads[^1], (0, 50), (100, 50));
        var bridge = map.RoadCells.Where(index => !map.TerrainAt(index % map.Width, index / map.Width).Buildable).Order().ToArray();
        Assert.Equal(2, bridge.Length);

        CityMapGeometry.Grid(map, (_, _) => true, roads[0], roads[1]);

        Assert.Equal(bridge, map.RoadCells.Where(index => !map.TerrainAt(index % map.Width, index / map.Width).Buildable).Order());
        foreach (int index in bridge)
        {
            int x = index % map.Width, y = index / map.Width;
            Assert.Equal(2, RoadNetwork.Neighbors.Count(direction => map.HasRoad(x + direction.Dx, y + direction.Dy)));
        }
    }

    [Theory]
    [InlineData(CityScenario.SanFrancisco)]
    [InlineData(CityScenario.LosAngeles)]
    [InlineData(CityScenario.SanDiego)]
    [InlineData(CityScenario.Chicago)]
    [InlineData(CityScenario.StLouis)]
    public void WaterRoadsAreUnbranchedBridgesWithLandAtBothEnds(CityScenario scenario)
    {
        var game = CityGame.New(new GameConfig { Scenario = scenario, Seed = 42 });
        var map = game.Map;
        var water = map.RoadCells.Where(index => !map.TerrainAt(index % map.Width, index / map.Width).Buildable).ToHashSet();
        Assert.NotEmpty(water);
        while (water.Count > 0)
        {
            int start = water.Min();
            var bridge = new List<int> { start };
            var banks = new HashSet<int>();
            water.Remove(start);
            for (int head = 0; head < bridge.Count; head++)
            {
                int index = bridge[head];
                int x = index % map.Width, y = index / map.Width;
                int connections = 0;
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!map.HasRoad(nx, ny)) continue;
                    connections++;
                    int neighbor = map.Index(nx, ny);
                    if (map.TerrainAt(nx, ny).Buildable) banks.Add(neighbor);
                    else if (water.Remove(neighbor)) bridge.Add(neighbor);
                }
                Assert.True(connections == 2, $"{scenario}: water road at ({x},{y}) has {connections} connections.");
            }
            Assert.True(banks.Count == 2, $"{scenario}: bridge at {map.PosOf(start)} has {banks.Count} banks.");
        }
    }
}
