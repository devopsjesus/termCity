using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class EdgeScrollerTests
{
    [Theory]
    [InlineData(0, 10, -1, 3)]
    [InlineData(1, 10, -1, 2)]
    [InlineData(2, 10, -1, 1)]
    [InlineData(3, 10, 0, 0)]
    [InlineData(40, 10, 0, 0)]
    [InlineData(86, 10, 0, 0)]
    [InlineData(87, 10, 1, 1)]
    [InlineData(88, 10, 1, 2)]
    [InlineData(89, 10, 1, 3)]
    public void HorizontalBandsAreThreeCellsWideAndFasterNearerTheEdge(int x, int y, int dx, int depth)
    {
        var zone = EdgeScroller.Zone(x, y, 90, 28);
        Assert.Equal(dx, zone.Dx);
        Assert.Equal(depth, zone.DepthX);
        Assert.Equal(0, zone.Dy);
    }

    [Theory]
    [InlineData(0, -1, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(25, 0, 0)]
    [InlineData(26, 1, 1)]
    [InlineData(27, 1, 2)]
    public void VerticalBandsAreTwoRowsTall(int y, int dy, int depth)
    {
        var zone = EdgeScroller.Zone(40, y, 90, 28);
        Assert.Equal(dy, zone.Dy);
        Assert.Equal(depth, zone.DepthY);
    }

    [Fact]
    public void FractionalSpeedsAccumulateIntoWholeCells()
    {
        var scroller = new EdgeScroller();
        // Depth 1 horizontally is 12 cells per second: 40 ms ticks give 0.48 cells each.
        int total = 0;
        for (int i = 0; i < 25; i++)
        {
            total += scroller.Step(2, 10, 90, 28, 0.04).Dx;
        }

        // 25 ticks of 40 ms at 12 cells per second is 12 cells, give or take floating point.
        Assert.InRange(total, -12, -11);
    }

    [Fact]
    public void CornersScrollDiagonallyAndTheEdgeIsFasterThanTheInnerBand()
    {
        var corner = new EdgeScroller().Step(0, 0, 90, 28, 1.0);
        Assert.True(corner.Dx < 0 && corner.Dy < 0);

        int inner = new EdgeScroller().Step(2, 10, 90, 28, 1.0).Dx;
        int outer = new EdgeScroller().Step(0, 10, 90, 28, 1.0).Dx;
        Assert.True(outer < inner, $"outer={outer} inner={inner}");
    }

    [Fact]
    public void LeavingTheBandResetsAndStopsScrolling()
    {
        var scroller = new EdgeScroller();
        scroller.Step(1, 10, 90, 28, 0.03);
        Assert.Equal((0, 0), scroller.Step(40, 10, 90, 28, 1.0));
        // The leftover fraction from before does not leak into the next visit.
        Assert.Equal(0, scroller.Step(2, 10, 90, 28, 0.01).Dx);
    }

    [Fact]
    public void TinyViewsDoNotScrollOnAnAxisThatWouldOverlap()
    {
        var zone = EdgeScroller.Zone(3, 1, 7, 4);
        Assert.Equal(0, zone.Dx);
        Assert.Equal(0, zone.Dy);
    }
}

public class PanSessionTests
{
    private static GameSession NewSession()
    {
        var s = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        s.SetViewport(80, 24);
        s.CenterOn(new Pos(80, 24));
        return s;
    }

    [Fact]
    public void PanKeepsTheGrabbedCellUnderThePointer()
    {
        var s = NewSession();
        var anchor = s.ScreenToMap(30, 10);
        s.PanCamera(anchor, 40, 12);
        Assert.Equal(anchor, s.ScreenToMap(40, 12));
    }

    [Fact]
    public void PanIsClampedToTheMap()
    {
        var s = NewSession();
        s.PanCamera(new Pos(0, 0), 70, 20);
        Assert.Equal(0, s.CameraX);
        Assert.Equal(0, s.CameraY);
        s.PanCamera(new Pos(s.Game.Map.Width - 1, s.Game.Map.Height - 1), 0, 0);
        Assert.Equal(s.Game.Map.Width - 80, s.CameraX);
        Assert.Equal(s.Game.Map.Height - 24, s.CameraY);
    }

    [Fact]
    public void PlaceCursorClearsSelectionAndDoesNotScroll()
    {
        var s = NewSession();
        s.BeginDrag(new Pos(10, 10));
        s.UpdateDrag(new Pos(12, 12));
        s.EndSelection();
        int cx = s.CameraX, cy = s.CameraY;
        s.PlaceCursor(new Pos(s.CameraX + 1, s.CameraY + 1));
        Assert.Null(s.Selection);
        Assert.Equal(new Pos(cx + 1, cy + 1), s.Cursor);
        Assert.Equal(cx, s.CameraX);
        Assert.Equal(cy, s.CameraY);
    }

    [Fact]
    public void EdgeScrollCanBeToggled()
    {
        var s = NewSession();
        Assert.False(s.EdgeScrollEnabled); // off until asked for
        s.ToggleEdgeScroll();
        Assert.True(s.EdgeScrollEnabled);
        s.ToggleEdgeScroll();
        Assert.False(s.EdgeScrollEnabled);
    }
}
