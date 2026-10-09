using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ScenarioBridgeTests
{
    private static GameMap River()
    {
        var map = new GameMap(16, 16, new GameContent());
        var water = map.Content.Terrains.Get(DefaultTerrains.WaterName);
        for (int y = 0; y < map.Height; y++)
            for (int x = 7; x <= 8; x++) map.SetTerrain(x, y, water);
        return map;
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
