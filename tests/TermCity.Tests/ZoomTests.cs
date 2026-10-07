using TermCity.Core.Roads;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ZoomTests
{
    private static GameSession NewSession(int w = 160, int h = 48)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = w, MapHeight = h });
        var s = new GameSession(game, Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        s.SetViewport(80, 24);
        s.CenterOn(new Pos(w / 2, h / 2));
        return s;
    }

    [Fact]
    public void ZoomOutSamplesMoreCellsAndZoomLabelsMatchTheRenderer()
    {
        var s = NewSession();
        Assert.Equal(80, s.ViewRect.Width);

        s.ZoomBy(-1);
        Assert.Equal(2, s.Stride);
        Assert.Equal(160, s.ViewRect.Width);
        Assert.Equal(48, s.ViewRect.Height);

        s.ZoomBy(-1);
        Assert.Equal(4, s.Stride);
        Assert.Equal(320, s.VisibleCellsX);

        s.SetZoom(0);
        s.ZoomBy(1);
        Assert.Equal(80, s.VisibleCellsX);
        Assert.Equal(24, s.VisibleCellsY);
        Assert.Equal("2x", s.ZoomLabel);
        s.SetZoom(0);
        Assert.Equal("1x", s.ZoomLabel);
        s.SetZoom(-1);
        Assert.Equal("0.5x", s.ZoomLabel);
        s.SetZoom(-2);
        Assert.Equal("0.25x", s.ZoomLabel);
        s.SetZoom(-3); // beyond the limit: stays at the furthest zoom
        Assert.Equal("0.25x", s.ZoomLabel);
    }

    [Fact]
    public void ZoomIsLimitedAtBothEnds()
    {
        var s = NewSession();
        for (int i = 0; i < 10; i++) s.ZoomBy(-1);
        Assert.Equal(GameSession.MinZoom, s.ZoomLevel);
        Assert.Equal(-2, GameSession.MinZoom);
        Assert.Equal(4, s.Stride);
        for (int i = 0; i < 10; i++) s.ZoomBy(1);
        Assert.Equal(GameSession.MaxZoom, s.ZoomLevel);
    }

    [Fact]
    public void ZoomingKeepsTheCellUnderThePointerInPlace()
    {
        var s = NewSession(640, 192);
        var before = s.ScreenToMap(30, 10);
        s.ZoomBy(1, 30, 10);
        Assert.Equal(before, s.ScreenToMap(30, 10));

        s.SetZoom(0, 30, 10);
        s.ZoomBy(-1, 60, 5);
        var after = s.ScreenToMap(60, 5);
        s.ZoomBy(1, 60, 5);
        // Out and back in returns to the same cell under the pointer (blocks snap to multiples of the stride).
        Assert.True(Math.Abs(s.ScreenToMap(60, 5).X - after.X) <= 2);
    }

    [Fact]
    public void ZoomedOutTheCameraSnapsToWholeBlocks()
    {
        var s = NewSession(640, 192);
        s.SetZoom(-2);
        s.ScrollCamera(7, 5);
        Assert.Equal(0, s.CameraX % 4);
        Assert.Equal(0, s.CameraY % 4);
        var p = s.ScreenToMap(3, 2);
        Assert.Equal(s.CameraX + 12, p.X);
        Assert.Equal(s.CameraY + 8, p.Y);
    }

    [Fact]
    public void ScrollingByCharactersMovesWholeBlocksWhenZoomedOutAndNeverStallsWhenZoomedIn()
    {
        var s = NewSession(640, 192);
        s.SetZoom(-1);
        int x = s.CameraX;
        s.ScrollChars(3, 0);
        Assert.Equal(x + 6, s.CameraX);

        s.SetZoom(1);
        x = s.CameraX;
        s.ScrollChars(1, 0);
        Assert.Equal(x + 1, s.CameraX);
        s.ScrollChars(-5, 0);
        Assert.Equal(x - 4, s.CameraX);
    }

    [Fact]
    public void ZoomedOutSelectionsCoverWholeBlocks()
    {
        var s = NewSession(640, 192);
        s.SetZoom(-2);
        var a = s.ScreenToMap(5, 3);
        var b = s.ScreenToMap(7, 4);
        s.BeginDrag(a);
        s.UpdateDrag(b);
        s.EndSelection();

        var sel = s.Selection!.Value;
        Assert.Equal(a.X, sel.X);
        Assert.Equal(a.Y, sel.Y);
        Assert.Equal((7 - 5 + 1) * 4, sel.Width);
        Assert.Equal((4 - 3 + 1) * 4, sel.Height);

        // A single click selects the block, and actions apply to it.
        s.SelectCell(a);
        Assert.Equal(new CellRect(a.X, a.Y, 4, 4), s.Selection);
        Assert.True(s.Zone(ZoneType.Residential).Success);
        Assert.Equal(16, s.Game.Stats.Residential.Zoned);
    }

    [Fact]
    public void ArrowKeysMoveOneBlockWhenZoomedOut()
    {
        var s = NewSession(640, 192);
        s.SetZoom(-2);
        var start = s.Cursor;
        s.MoveCursor(1, 0);
        Assert.Equal(start.X + 4, s.Cursor.X);
        s.MoveCursor(0, 1);
        Assert.Equal(start.Y + 4, s.Cursor.Y);
    }

    [Fact]
    public void PanKeepsTheGrabbedCellUnderThePointerWhenZoomedInAndOut()
    {
        foreach (int level in new[] { -2, -1, 0, 1 })
        {
            var s = NewSession(640, 192);
            s.SetZoom(level);
            var anchor = s.ScreenToMap(30, 10);
            s.PanCamera(anchor, 40, 12);
            Assert.Equal(anchor, s.ScreenToMap(40, 12));
        }
    }

    [Fact]
    public void SmallMapsFitWhenZoomedOutFarEnough()
    {
        var s = NewSession(160, 48);
        s.SetZoom(-3);
        Assert.Equal(0, s.CameraX);
        Assert.Equal(0, s.CameraY);
        Assert.True(s.VisibleCellsX >= 160);
    }

    [Fact]
    public void BlockSamplerPrefersBuildingsThenZonesThenRoadsThenWater()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var water = map.Content.Terrains.Get("Water");
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);

        // A 4x4 block at (40,5): water in one cell, a highway in another, then a zone, then a building.
        map.SetTerrain(41, 6, water);
        var sampler = new BlockSampler(game);
        Assert.Equal(CellRenderer.Render(game, 41, 6), sampler.Sample(40, 5, 4));

        map.SetRoad(42, 7, highway);
        game.Touch();
        Assert.Equal(highway.GlyphFor(0), sampler.Sample(40, 5, 4).Glyph);

        map.SetZone(43, 8, ZoneType.Commercial);
        game.Touch();
        Assert.Equal(CellRenderer.Render(game, 43, 8), sampler.Sample(40, 5, 4));

        map.SetBuilding(40, 5, map.Content.Buildings.ForZone(ZoneType.Commercial));
        game.Touch();
        Assert.Equal(CellRenderer.Render(game, 40, 5), sampler.Sample(40, 5, 4));
    }

    [Fact]
    public void BlockSamplerClipsAtTheMapEdge()
    {
        var game = TestCity.Flat();
        var sampler = new BlockSampler(game);
        var visual = sampler.Sample(158, 46, 8);
        Assert.False(string.IsNullOrEmpty(visual.Glyph));
    }
}
