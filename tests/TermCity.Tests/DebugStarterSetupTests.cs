using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class DebugStarterSetupTests
{
    [Theory]
    [InlineData(1, 160, 96)]
    [InlineData(42, 160, 96)]
    [InlineData(1, 320, 192)]
    [InlineData(42, 320, 192)]
    public void StarterPlacesExactlyTheRequestedSettlementAndRunsTheClock(int seed, int width, int height)
    {
        var game = CityGame.New(new GameConfig { Seed = seed, MapWidth = width, MapHeight = height });
        game.Paused = true;
        if (!CityGame.DebugToolsAvailable)
        {
            Assert.Throws<InvalidOperationException>(() => DebugStarterSetup.Apply(game));
            return;
        }
        var roads = game.Map.RoadTypeLayer.ToArray();
        var terrain = game.Map.TerrainLayer.ToArray();
        int money = game.Money;
        var setup = DebugStarterSetup.Apply(game);
        var map = game.Map;

        Assert.Equal(HighwayGenerator.StartingSpur(map), setup.Spur);
        Assert.Equal(4, setup.Residential.Width);
        Assert.Equal(10, setup.Residential.Height);
        Assert.Equal(40, map.ZoneCells(ZoneType.Residential).Count());
        Assert.All(setup.Residential.Cells(), cell =>
        {
            Assert.Equal(ZoneType.Residential, map.ZoneAt(cell.X, cell.Y));
            Assert.Null(map.BuildingAt(cell.X, cell.Y));
            Assert.Null(map.FeatureAt(cell.X, cell.Y));
        });
        Assert.Equal("Town Well", map.BuildingAt(setup.Well.X, setup.Well.Y)!.Name);
        Assert.Equal("Woodlot", map.BuildingAt(setup.Woodlot.X, setup.Woodlot.Y)!.Name);
        Assert.Equal(2, map.BuildingFootprints.Count);
        Assert.Equal(2, map.ServiceCells.Count);
        Assert.InRange(Math.Abs(setup.Well.X - setup.Spur.X) + Math.Abs(setup.Well.Y - setup.Spur.Y), 1, 8);
        Assert.InRange(Math.Abs(setup.Woodlot.X - setup.Spur.X) + Math.Abs(setup.Woodlot.Y - setup.Spur.Y), 1, 8);
        Assert.InRange(Math.Abs(setup.Residential.X - setup.Spur.X) + Math.Abs(setup.Residential.Y - setup.Spur.Y), 1, 32);
        Assert.Equal(roads, map.RoadTypeLayer);
        Assert.Equal(terrain, map.TerrainLayer);
        Assert.True(game.Money < money);

        var session = new GameSession(game, initialCameraTarget: setup.CameraTarget);
        session.SetCameraEdgePadding(true);
        Assert.Equal(new(setup.Residential.X + 2, setup.Residential.Y + 5), setup.CameraTarget);
        Assert.Equal(setup.CameraTarget, session.Cursor);
        foreach (var (columns, rows) in new[] { (18, 12), (42, 24), (23, 17), (18, 12) })
        {
            session.SetViewport(columns, rows);
            Assert.Equal(setup.CameraTarget.X, session.CameraX + session.VisibleCellsX / 2);
            Assert.Equal(setup.CameraTarget.Y, session.CameraY + session.VisibleCellsY / 2);
            Assert.False(session.HasCursor);
            Assert.Null(session.Selection);
        }
        Assert.False(session.GuideVisible);
        Assert.False(game.Paused);
        Assert.False(session.ClockSuspended);
        double elapsed = game.ElapsedDays;
        session.Update(0.5);
        Assert.True(game.ElapsedDays > elapsed);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(setup.Residential.Cells().Select(cell => map.Index(cell.X, cell.Y)).Order(),
            loaded.Map.ZoneCells(ZoneType.Residential).Order());
    }

    [Fact]
    public void StarterCameraDoesNotOverrideLaterUserNavigationOrSavedViews()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        var setup = DebugStarterSetup.Apply(game);
        var session = new GameSession(game, initialCameraTarget: setup.CameraTarget);
        session.SetCameraEdgePadding(true);
        session.SetViewport(18, 12);
        session.ScrollCamera(40, 20);
        int x = session.CameraX, y = session.CameraY;
        session.SetViewport(20, 14);
        Assert.Equal(x, session.CameraX);
        Assert.Equal(y, session.CameraY);
        Assert.NotEqual(setup.CameraTarget.X, session.CameraX + session.VisibleCellsX / 2);
        session.CenterOn(new(30, 30));
        session.SetViewport(22, 16);
        Assert.NotEqual(setup.CameraTarget.X, session.CameraX + session.VisibleCellsX / 2);
        string path = Path.Combine(Path.GetTempPath(), "termcity-starter-focus-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SaveGameStore.Save(game, path, new SavedView(12, 14, 0) { CameraEdgePadding = true });
            Assert.True(session.LoadFrom(path));
            session.SetViewport(24, 18);
            Assert.Equal(12, session.CameraX);
            Assert.Equal(14, session.CameraY);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedStarterSetupLeavesTheCityUnchanged()
    {
        var noSpur = TestCity.Flat();
        string original = SaveGameStore.Serialize(noSpur);
        Assert.Throws<InvalidOperationException>(() => DebugStarterSetup.Apply(noSpur));
        Assert.Equal(original, SaveGameStore.Serialize(noSpur));

        var poor = CityGame.New(new GameConfig { Seed = 42, StartingMoney = 1 });
        original = SaveGameStore.Serialize(poor);
        Assert.Throws<InvalidOperationException>(() => DebugStarterSetup.Apply(poor));
        Assert.Equal(original, SaveGameStore.Serialize(poor));

        var occupied = TestCity.Flat();
        occupied.Map.SetZone(10, 10, ZoneType.Residential);
        original = SaveGameStore.Serialize(occupied);
        Assert.Throws<InvalidOperationException>(() => DebugStarterSetup.Apply(occupied));
        Assert.Equal(original, SaveGameStore.Serialize(occupied));
    }

    [Fact]
    public void StarterIsAnExplicitDebugOnlyStartupOption()
    {
        Assert.False(GodotOptions.Parse([]).DebugStarter);
        if (CityGame.DebugToolsAvailable)
        {
            var options = GodotOptions.Parse(["--debug-starter", "--seed", "42", "--size", "small"]);
            Assert.True(options.DebugStarter);
            Assert.False(options.SmokeTest);
            Assert.False(options.Load);
            Assert.Equal(42, options.Config.Seed);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--debug-starter"]));
        }
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--debug-starter", "--load"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--debug-starter", "--size", "SF"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--debug-starter", "--smoke-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--debug-starter", "--dump-map"]));
    }
}
