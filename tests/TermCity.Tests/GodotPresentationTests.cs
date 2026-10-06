using TermCity.Core.Rendering;
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
    public void ZoomedInCellsAreDuplicatedAndResizeChangesViewport()
    {
        var session = Session();
        var grid = new TerminalGrid();
        session.SetZoom(1);
        grid.Resize(120, 66);
        grid.Fill(session);
        Assert.Equal(grid.VisualAt(session, 0, 0), grid.VisualAt(session, 1, 0));
        grid.Resize(240, 110);
        grid.Fill(session);
        Assert.Equal(20, session.ViewWidth);
        Assert.Equal(5, session.ViewHeight);
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
    public void PrototypeOptionsReuseMapSizeValidation()
    {
        var options = PrototypeOptions.Parse(["--seed", "-42", "--size", "large", "--smoke-test"]);
        Assert.Equal(-42, options.Config.Seed);
        Assert.Equal(640, options.Config.MapWidth);
        Assert.Equal(384, options.Config.MapHeight);
        Assert.True(options.SmokeTest);
        Assert.Throws<ArgumentException>(() => PrototypeOptions.Parse(["--size", "79x24"]));
        Assert.Throws<ArgumentException>(() => PrototypeOptions.Parse(["--seed", "2147483648"]));
        Assert.Throws<ArgumentException>(() => PrototypeOptions.Parse(["--seed"]));
        Assert.Throws<ArgumentException>(() => PrototypeOptions.Parse(["--unknown"]));
        Assert.Throws<ArgumentException>(() => PrototypeOptions.Parse(["--capture", "screen.png"]));
    }
}
