using TermCity.Core.Persistence;
using TermCity.Core.Session;

namespace TermCity.Tests;

public class SavedViewTests
{
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
