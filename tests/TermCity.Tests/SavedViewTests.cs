using TermCity.Core.Persistence;
using TermCity.Core.Session;

namespace TermCity.Tests;

public class SavedViewTests
{
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void PerspectivePanningCanCenterAllMapEdgesWithoutMovingCursorOutsideMap(int zoom)
    {
        var session = new GameSession(TestCity.Flat());
        session.SetViewport(30, 16);
        session.SetZoom(zoom);
        session.SetCameraEdgePadding(true);
        foreach (var edge in new[] { new TermCity.Core.Util.Pos(0, 0),
            new TermCity.Core.Util.Pos(session.Game.Map.Width - 1, session.Game.Map.Height - 1) })
        {
            session.CenterOn(edge);
            var center = session.ScreenToMap(session.ViewWidth / 2, session.ViewHeight / 2);
            Assert.InRange(Math.Abs(center.X - edge.X), 0, session.Stride);
            Assert.InRange(Math.Abs(center.Y - edge.Y), 0, session.Stride);
            Assert.True(session.Game.Map.InBounds(session.Cursor));
        }
        session.CenterOn(new(0, 0));
        Assert.True(session.CameraX < 0 && session.CameraY < 0);
        string json = SaveGameStore.Serialize(session.Game, new SavedView(session.CameraX, session.CameraY, zoom)
            { CameraEdgePadding = true });
        SaveGameStore.Deserialize(json, out var saved);
        Assert.True(saved!.CameraEdgePadding);
        Assert.Equal(session.CameraX, saved.CameraX);
        session.SetCameraEdgePadding(false);
        Assert.True(session.CameraX >= 0 && session.CameraY >= 0);
    }

    [Fact]
    public void NegativePaddedViewRoundTripsThroughSessionLoading()
    {
        string path = Path.Combine(Path.GetTempPath(), $"termcity-padded-view-{Guid.NewGuid():N}.json");
        try
        {
            var session = new GameSession(TestCity.Flat(), path);
            session.SetViewport(30, 16);
            session.SetCameraEdgePadding(true);
            session.CenterOn(new(0, 0));
            int x = session.CameraX, y = session.CameraY;
            Assert.True(session.QuickSave());
            session.SetCameraEdgePadding(false);
            Assert.True(session.QuickLoad());
            Assert.True(session.CameraEdgePadding);
            Assert.Equal((x, y), (session.CameraX, session.CameraY));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void SaveAndLoadRestoreCameraAndZoom(int zoom)
    {
        string path = Path.Combine(Path.GetTempPath(), $"termcity-view-{Guid.NewGuid():N}.json");
        try
        {
            var session = new GameSession(TestCity.Flat(), path);
            session.SetViewport(30, 15);
            session.SetZoom(zoom);
            session.ScrollCamera(16, 8);
            int x = session.CameraX, y = session.CameraY;
            Assert.True(session.QuickSave());
            session.SetZoom(0);
            session.CenterOn(new(0, 0));
            Assert.True(session.LoadFrom(path));
            Assert.Equal(zoom, session.ZoomLevel);
            Assert.Equal(x, session.CameraX);
            Assert.Equal(y, session.CameraY);
            Assert.True(session.Game.Paused);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacySavesWithoutViewRemainLoadable()
    {
        string json = SaveGameStore.Serialize(TestCity.Flat());
        SaveGameStore.Deserialize(json, out var view);
        Assert.Null(view);
    }

    [Fact]
    public void InvalidViewIsRejected()
    {
        string json = SaveGameStore.Serialize(TestCity.Flat(), new SavedView(-1, 0, 0));
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json));
    }
}
