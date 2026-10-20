using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Persistence;

namespace TermCity.Tests;

public class WaterfrontStarterRoadTests
{
    [Theory]
    [InlineData(160, 96)]
    [InlineData(320, 192)]
    [InlineData(640, 384)]
    public void RandomMapsHaveAConnectedStarterSpokeNearWaterWithRoomToSettle(int width, int height)
    {
        foreach (int seed in new[] { 1, 3, 7, 17, 42, 63, 137 })
        {
            var map = MapGenerator.Generate(width, height, seed, new GameContent());
            var geometry = new RoadGeometry(map, RoadCurves.Extract(map, (_, _) => true));
            var ends = map.RoadCells.Select(map.PosOf).Where(p =>
                map.RoadTypeAt(p.X, p.Y)?.Name == DefaultRoads.TrackName &&
                Neighbors(map, p).Count(n => map.HasRoad(n.X, n.Y)) == 1).ToArray();
            Assert.True(ends.Any(end => NearWater(map, end) && HasSettlementPlot(map, geometry, end) &&
                ReachesHighway(map, end)), $"Seed {seed}, {width}x{height}: no usable waterfront highway spoke.");
            Assert.True(ends.Length >= 2, $"Seed {seed}, {width}x{height}: only {ends.Length} waterfront spurs.");
            Assert.InRange(ends.Length, 2, 6);
            var start = HighwayGenerator.StartingSpur(map);
            Assert.NotNull(start);
            Assert.Contains(start.Value, ends);
            int distance = Math.Abs(start.Value.X - width / 2) + Math.Abs(start.Value.Y - height / 2);
            Assert.Equal(ends.Min(p => Math.Abs(p.X - width / 2) + Math.Abs(p.Y - height / 2)), distance);
            Assert.All(ends, end =>
            {
                Assert.True(NearWater(map, end), $"Spur {end} must end near a body of water.");
                Assert.True(HasSettlementPlot(map, geometry, end), $"Spur {end} must leave room to settle.");
                Assert.True(ReachesHighway(map, end), $"Spur {end} must connect to a King's Road.");
            });
        }
    }

    [Theory]
    [InlineData(160, 96)]
    [InlineData(320, 192)]
    [InlineData(640, 384)]
    public void WaterfrontSpokeGenerationIsDeterministic(int width, int height)
    {
        var first = MapGenerator.Generate(width, height, 42, new GameContent());
        var second = MapGenerator.Generate(width, height, 42, new GameContent());
        Assert.Equal(first.RoadLayer, second.RoadLayer);
        Assert.Equal(first.RoadTypeLayer, second.RoadTypeLayer);
    }

    [Fact]
    public void NewSessionAndRestartCenterOnTheMostCentralWaterfrontSpur()
    {
        var session = new GameSession(CityGame.New(new GameConfig { Seed = 42, MapWidth = 320, MapHeight = 192 }));
        var expected = HighwayGenerator.StartingSpur(session.Game.Map);
        Assert.NotNull(expected);
        Assert.Equal(expected.Value, session.Cursor);
        session.SetCameraEdgePadding(true);
        session.SetViewport(24, 16);
        Assert.Equal(expected.Value.X, session.CameraX + session.VisibleCellsX / 2);
        Assert.Equal(expected.Value.Y, session.CameraY + session.VisibleCellsY / 2);
        session.NewGame(session.Game.Config with { Seed = 17 });
        expected = HighwayGenerator.StartingSpur(session.Game.Map);
        Assert.Equal(expected!.Value, session.Cursor);
        Assert.Equal(expected.Value.X, session.CameraX + session.VisibleCellsX / 2);
        Assert.Equal(expected.Value.Y, session.CameraY + session.VisibleCellsY / 2);
        Assert.Equal(1, session.ZoomLevel);
    }

    [Fact]
    public void StartingSpurPrefersTheCenterWithoutRequiringIt()
    {
        var map = new GameMap(96, 64, new GameContent());
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        var dirt = map.Content.Roads.Default;
        for (int x = 0; x < map.Width; x++) map.SetRoad(x, 20, highway);
        foreach (int x in new[] { 10, 48 })
        {
            for (int y = 21; y <= 30; y++) map.SetRoad(x, y, dirt);
            map.SetTerrain(x + 3, 30, map.Content.Terrains.Get("Water"));
        }
        Assert.Equal(new Pos(48, 30), HighwayGenerator.StartingSpur(map));
        for (int y = 21; y <= 30; y++) map.SetRoad(48, y, false);
        Assert.Equal(new Pos(10, 30), HighwayGenerator.StartingSpur(map));
    }

    [Fact]
    public void LoadingRetainsTheSavedCameraInsteadOfRecenteringOnASpur()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        string path = Path.Combine(Path.GetTempPath(), "termcity-spur-view-" + Guid.NewGuid().ToString("N") + ".json");
        SaveGameStore.Save(game, path, new SavedView(20, 20, 0) { CameraEdgePadding = true });
        try
        {
            var session = new GameSession(game);
            session.SetViewport(24, 16);
            Assert.True(session.LoadFrom(path));
            Assert.Equal(20, session.CameraX);
            Assert.Equal(20, session.CameraY);
            session.SetViewport(22, 14);
            Assert.NotEqual(HighwayGenerator.StartingSpur(game.Map)!.Value,
                new Pos(session.CameraX + session.VisibleCellsX / 2, session.CameraY + session.VisibleCellsY / 2));
        }
        finally { File.Delete(path); }
    }

    private static bool NearWater(GameMap map, Pos end) =>
        new CellRect(end.X - 6, end.Y - 6, 13, 13).Cells().Any(p => map.InBounds(p) &&
            Math.Abs(p.X - end.X) + Math.Abs(p.Y - end.Y) <= 6 &&
            map.TerrainAt(p.X, p.Y).Name == DefaultTerrains.WaterName);

    private static bool HasSettlementPlot(GameMap map, RoadGeometry geometry, Pos end) =>
        new[] { -4, 1 }.Any(dx => new[] { -4, 1 }.Any(dy =>
            new CellRect(end.X + dx, end.Y + dy, 4, 4).Cells().All(p => map.InBounds(p) &&
                map.TerrainAt(p.X, p.Y).Buildable &&
                map.TerrainAt(p.X, p.Y).Name != DefaultTerrains.HillName &&
                !geometry.OverlapsCell(p.X, p.Y))));

    private static bool ReachesHighway(GameMap map, Pos end)
    {
        var visited = new HashSet<Pos> { end };
        var queue = new Queue<Pos>();
        queue.Enqueue(end);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Neighbors(map, cell).Where(p => map.HasRoad(p.X, p.Y)))
            {
                if (map.RoadTypeAt(next.X, next.Y)?.Name == DefaultRoads.KingsRoadName) return true;
                if (visited.Add(next)) queue.Enqueue(next);
            }
        return false;
    }

    private static IEnumerable<Pos> Neighbors(GameMap map, Pos cell) =>
        new[] { new Pos(cell.X - 1, cell.Y), new Pos(cell.X + 1, cell.Y),
            new Pos(cell.X, cell.Y - 1), new Pos(cell.X, cell.Y + 1) }.Where(map.InBounds);
}
