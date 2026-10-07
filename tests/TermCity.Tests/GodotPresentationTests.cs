using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class GodotPresentationTests
{
    private static GameSession Session() => new(TestCity.Flat(config: new GameConfig { MapWidth = 160, MapHeight = 96 }),
        Path.Combine(Path.GetTempPath(), "termcity-godot-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public void PixelMappingUsesWholeCellsAndRejectsOutsideCoordinates()
    {
        var grid = new TerminalGrid();
        grid.Resize(125, 71);
        Assert.Equal(10, grid.Columns);
        Assert.Equal(3, grid.Rows);
        Assert.True(grid.TryCell(12, 22, out var cell));
        Assert.Equal(new Pos(1, 1), cell);
        Assert.True(grid.TryCell(119, 65, out cell));
        Assert.Equal(new Pos(9, 2), cell);
        Assert.False(grid.TryCell(-1, 0, out _));
        Assert.False(grid.TryCell(0, -1, out _));
        Assert.False(grid.TryCell(120, 0, out _));
        Assert.False(grid.TryCell(0, 66, out _));
        Assert.False(grid.TryCell(float.NaN, 0, out _));
        Assert.False(grid.TryCell(0, float.PositiveInfinity, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Resize(-1, 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 10)]
    public void SharedMinimapRejectsInvalidDimensions(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapImage().Update(Session().Game, width, height));

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void GridUsesSharedRendererAndZoomSampling(int zoom)
    {
        var session = Session();
        var grid = new TerminalGrid();
        grid.Resize(120, 66);
        session.SetZoom(zoom);
        grid.Fill(session);
        session.PlaceCursor(new Pos(159, 95));
        grid.Fill(session);
        var sampler = new BlockSampler(session.Game);
        for (int y = 0; y < grid.Rows; y++)
        {
            for (int x = 0; x < grid.Columns; x++)
            {
                var position = session.ScreenToMap(x, y);
                var expected = zoom < 0
                    ? sampler.Sample(position.X, position.Y, session.Stride)
                    : CellRenderer.Render(session.Game, position.X, position.Y);
                Assert.Equal(expected, grid.VisualAt(session, x, y));
            }
        }
        Assert.Equal(grid.Columns, session.ViewWidth);
        Assert.Equal(grid.Rows, session.ViewHeight);
    }

    [Fact]
    public void ZoomedInCellsScaleBothAxesWithoutDuplicatingCells()
    {
        var session = Session();
        var grid = new TerminalGrid();
        session.SetZoom(1);
        grid.Resize(120, 66);
        grid.Fill(session);
        Assert.Equal(24, grid.PixelWidth);
        Assert.Equal(44, grid.PixelHeight);
        Assert.Equal(5, session.VisibleCellsX);
        Assert.Equal(1, session.VisibleCellsY);
        Assert.Equal("2x", session.ZoomLabel);
        grid.Resize(240, 110);
        grid.Fill(session);
        Assert.Equal(10, session.ViewWidth);
        Assert.Equal(2, session.ViewHeight);
        Assert.True(grid.TryCell(24, 44, out var cell));
        Assert.Equal(new Pos(1, 1), cell);
        session.PlaceCursor(session.ScreenToMap(0, 0));
        grid.Fill(session);
        Assert.NotEqual(grid.VisualAt(session, 0, 0)!.Value.Background,
            grid.VisualAt(session, 1, 0)!.Value.Background);
        session.SetZoom(0);
        grid.Fill(session);
        Assert.Equal(12, grid.PixelWidth);
        Assert.Equal(22, grid.PixelHeight);
        Assert.Equal(20, session.VisibleCellsX);
        Assert.Equal(5, session.VisibleCellsY);
    }

    [Fact]
    public void BlankMapMarginsAreNotRendered()
    {
        var session = Session();
        var grid = new TerminalGrid();
        grid.Resize(12 * 180, 22 * 110);
        grid.Fill(session);
        Assert.Null(grid.VisualAt(session, 160, 0));
        Assert.Null(grid.VisualAt(session, 0, 96));
    }

    [Fact]
    public void SelectionPreviewAndCursorOverlayInPriorityOrder()
    {
        var session = Session();
        var grid = new TerminalGrid();
        grid.Resize(120, 66);
        grid.Fill(session);
        var start = session.ScreenToMap(1, 1);
        var end = session.ScreenToMap(3, 1);
        session.BeginDrag(start);
        session.UpdateDrag(end);
        session.EndSelection();
        Assert.Equal(Rgb.Hex(0x4e4578), grid.VisualAt(session, 1, 1)!.Value.Background);
        Assert.Equal(Rgb.Hex(0xffdc5a), grid.VisualAt(session, 3, 1)!.Value.Background);
        session.PreviewRoad();
        Assert.Equal(Rgb.Hex(0x2a7849), grid.VisualAt(session, 1, 1)!.Value.Background);
        session.Game.Map.SetTerrain(start.X, start.Y, session.Game.Map.Content.Terrains.Get("Water"));
        session.Game.Touch();
        Assert.Equal(Rgb.Hex(0x8c2d37), grid.VisualAt(session, 1, 1)!.Value.Background);
        Assert.Equal(Rgb.Hex(0xffdc5a), grid.VisualAt(session, 3, 1)!.Value.Background);
    }

    [Fact]
    public void GodotOptionsParseReducedMotion()
    {
        Assert.False(GodotOptions.Parse([]).ReducedMotion);
        Assert.True(GodotOptions.Parse(["--reduced-motion"]).ReducedMotion);
        Assert.Contains("--reduced-motion", GodotOptions.Usage);
    }

    [Fact]
    public void GodotOptionsReuseMapSizeValidation()
    {
        var options = GodotOptions.Parse(["--seed", "-42", "--size", "large", "--smoke-test"]);
        Assert.Equal(-42, options.Config.Seed);
        Assert.Equal(640, options.Config.MapWidth);
        Assert.Equal(384, options.Config.MapHeight);
        Assert.True(options.SmokeTest);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--size", "79x24"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--seed", "2147483648"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--seed"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--unknown"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--capture", "screen.png"]));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    [InlineData("60", 60)]
    public void GodotOptionsAcceptFrameRateLimits(string value, int expected)
    {
        Assert.Equal(expected, GodotOptions.Parse(["--fps", value]).FramesPerSecond);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("61")]
    [InlineData("NaN")]
    public void GodotOptionsRejectInvalidFrameRates(string value) =>
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--fps", value]));

    [Fact]
    public void GodotOptionsSupportLoadingDumpingAndHelp()
    {
        var options = GodotOptions.Parse(["--load", "city with spaces.json", "--dump-map", "--help"]);
        Assert.True(options.Load);
        Assert.Equal("city with spaces.json", options.LoadPath);
        Assert.True(options.DumpMap);
        Assert.True(options.Help);
        options = GodotOptions.Parse(["--load", "--seed", "42"]);
        Assert.True(options.Load);
        Assert.Null(options.LoadPath);
        Assert.Equal(42, options.Config.Seed);
        Assert.Equal(30, options.FramesPerSecond);
    }

    [Theory]
    [InlineData(TerminalGrid.AnimationKind.Hill)]
    [InlineData(TerminalGrid.AnimationKind.Tree)]
    [InlineData(TerminalGrid.AnimationKind.Water)]
    public void AnimationHoldsFourStepsAtEightyBpmWithoutChangingGameplay(TerminalGrid.AnimationKind kind)
    {
        var session = Session();
        session.Game.Paused = true;
        var map = session.Game.Map;
        var position = Enumerable.Range(0, 100)
            .Select(x => new Pos(x, 2)).First(TerminalGrid.ShouldAnimate);
        if (kind == TerminalGrid.AnimationKind.Tree)
        {
            map.SetFeature(position.X, position.Y, map.Content.Features.Get("Tree"));
        }
        else
        {
            map.SetTerrain(position.X, position.Y, map.Content.Terrains.Get(kind.ToString()));
        }
        var grid = new TerminalGrid();
        grid.Resize(160 * TerminalGrid.CellWidth, 96 * TerminalGrid.CellHeight);
        grid.Fill(session);
        Assert.Equal(kind, grid.AnimationAt(position.X, position.Y));
        string saved = SaveGameStore.Serialize(session.Game);
        int rebuilds = grid.Rebuilds;
        var visual = grid.VisualAt(session, position.X, position.Y);
        float direction = ((position.X ^ position.Y) & 1) == 0 ? 1 : -1;
        foreach (float step in new[] { 1.5f, 0, -1.5f, 0, 1.5f })
        {
            var expected = kind == TerminalGrid.AnimationKind.Hill ? (0f, step * direction) : (step * direction, 0f);
            Assert.Equal(expected, grid.OffsetAt(session, position.X, position.Y));
            Assert.False(grid.AdvanceAnimation(TerminalGrid.BeatSeconds / 2, true));
            Assert.Equal(expected, grid.OffsetAt(session, position.X, position.Y));
            Assert.True(grid.AdvanceAnimation(TerminalGrid.BeatSeconds / 2, true));
        }
        double seconds = grid.AnimationSeconds;
        Assert.False(grid.AdvanceAnimation(1, false));
        Assert.Equal(seconds, grid.AnimationSeconds);
        Assert.Equal(rebuilds, grid.Rebuilds);
        Assert.Equal(visual, grid.VisualAt(session, position.X, position.Y));
        Assert.Equal(saved, SaveGameStore.Serialize(session.Game));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.AdvanceAnimation(double.NaN, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.AdvanceAnimation(-1, true));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void AnimationDensityIsEightToTwelvePercentForEachKindAtEveryZoom(int zoom)
    {
        var game = CityGame.New(new GameConfig { Seed = 42, MapWidth = 640, MapHeight = 384 });
        var session = new GameSession(game, "unused-godot-animation-test.json");
        var grid = new TerminalGrid();
        grid.Resize(640 * TerminalGrid.CellWidth, 384 * TerminalGrid.CellHeight);
        session.SetZoom(zoom);
        grid.Fill(session);
        var hills = game.Map.Content.Terrains.Get("Hill").Glyphs;
        var trees = game.Map.Content.Features.Get("Tree").Glyphs;
        var water = game.Map.Content.Terrains.Get("Water").Glyphs;
        int hillCount = 0, treeCount = 0, waterCount = 0, animatedHills = 0, animatedTrees = 0, animatedWater = 0;
        for (int y = 0; y < grid.Rows; y++)
        {
            for (int x = 0; x < grid.Columns; x++)
            {
                var visual = grid.VisualAt(session, x, y);
                if (visual is null)
                {
                    continue;
                }
                if (hills.Contains(visual.Value.Glyph))
                {
                    hillCount++;
                    animatedHills += grid.AnimationAt(x, y) == TerminalGrid.AnimationKind.Hill ? 1 : 0;
                }
                if (trees.Contains(visual.Value.Glyph))
                {
                    treeCount++;
                    animatedTrees += grid.AnimationAt(x, y) == TerminalGrid.AnimationKind.Tree ? 1 : 0;
                }
                if (water.Contains(visual.Value.Glyph))
                {
                    waterCount++;
                    animatedWater += grid.AnimationAt(x, y) == TerminalGrid.AnimationKind.Water ? 1 : 0;
                }
            }
        }
        Assert.True(hillCount > 0 && treeCount > 0 && waterCount > 0);
        Assert.InRange(100.0 * animatedHills / hillCount, 8, 12);
        Assert.InRange(100.0 * animatedTrees / treeCount, 8, 12);
        Assert.InRange(100.0 * animatedWater / waterCount, 8, 12);
    }

    [Theory]
    [InlineData(TerminalGrid.AnimationKind.Tree)]
    [InlineData(TerminalGrid.AnimationKind.Water)]
    public void AnimationEligibilityFollowsVisibleGlyphAndSurvivesScrolling(TerminalGrid.AnimationKind kind)
    {
        var session = Session();
        var map = session.Game.Map;
        var position = Enumerable.Range(40, 100)
            .Select(x => new Pos(x, 40)).First(TerminalGrid.ShouldAnimate);
        if (kind == TerminalGrid.AnimationKind.Tree)
            map.SetFeature(position.X, position.Y, map.Content.Features.Get("Tree"));
        else map.SetTerrain(position.X, position.Y, map.Content.Terrains.Get("Water"));
        var grid = new TerminalGrid();
        grid.Resize(120, 110);
        grid.Fill(session);
        session.CenterOn(position);
        grid.Fill(session);
        var screen = ScreenOf(session, grid, position);
        Assert.Equal(kind, grid.AnimationAt(screen.X, screen.Y));
        var before = grid.OffsetAt(session, screen.X, screen.Y);
        session.ScrollCamera(1, 0);
        grid.Fill(session);
        screen = ScreenOf(session, grid, position);
        Assert.Equal(before, grid.OffsetAt(session, screen.X, screen.Y));
        map.SetRoad(position.X, position.Y, true);
        grid.Fill(session);
        Assert.Equal(TerminalGrid.AnimationKind.None, grid.AnimationAt(screen.X, screen.Y));
        Assert.Equal((0f, 0f), grid.OffsetAt(session, screen.X, screen.Y));
    }

    [Theory]
    [InlineData(TerminalGrid.AnimationKind.Hill)]
    [InlineData(TerminalGrid.AnimationKind.Water)]
    public void NeighboringWorldTilesHaveOppositeAnimationDirections(TerminalGrid.AnimationKind kind)
    {
        var session = Session();
        var map = session.Game.Map;
        var center = Enumerable.Range(0, map.Width * (map.Height - 1))
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => p.X + 1 < map.Width && TerminalGrid.ShouldAnimate(p) &&
                TerminalGrid.ShouldAnimate(new Pos(p.X + 1, p.Y)));
        map.SetTerrain(center.X, center.Y, map.Content.Terrains.Get(kind.ToString()));
        map.SetTerrain(center.X + 1, center.Y, map.Content.Terrains.Get(kind.ToString()));
        var grid = new TerminalGrid();
        grid.Resize(map.Width * TerminalGrid.CellWidth, map.Height * TerminalGrid.CellHeight);
        grid.Fill(session);
        for (int beat = 0; beat < 4; beat++)
        {
            var offset = grid.OffsetAt(session, center.X, center.Y);
            Assert.Equal((-offset.X, -offset.Y), grid.OffsetAt(session, center.X + 1, center.Y));
            grid.AdvanceAnimation(TerminalGrid.BeatSeconds, true);
        }
    }

    private static Pos ScreenOf(GameSession session, TerminalGrid grid, Pos position) =>
        Enumerable.Range(0, grid.Columns * grid.Rows)
            .Select(i => new Pos(i % grid.Columns, i / grid.Columns))
            .First(p => session.ScreenToMap(p.X, p.Y) == position);
}
