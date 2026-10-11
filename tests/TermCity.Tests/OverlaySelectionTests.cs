using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class OverlaySelectionTests
{
    [Fact]
    public void ToggleRestoresLastChosenOverlayIncludingAfterClear()
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        session.ToggleOverlay();
        Assert.Equal(MapOverlay.Power, session.Overlay);
        session.SetOverlay(MapOverlay.Parks);
        session.ToggleOverlay();
        Assert.Equal(MapOverlay.Off, session.Overlay);
        session.ToggleOverlay();
        Assert.Equal(MapOverlay.Parks, session.Overlay);
        session.SetOverlay(MapOverlay.Off);
        session.ToggleOverlay();
        Assert.Equal(MapOverlay.Parks, session.Overlay);
    }

    [Fact]
    public void ToggleDoesNotEnableUnsupportedClassicOverlays()
    {
        var session = new GameSession(TestCity.Flat());
        session.ToggleOverlay();
        Assert.Equal(MapOverlay.Off, session.Overlay);
        Assert.Contains("full rules", session.Message);
    }

    [Fact]
    public void EveryOverlayCanBeSelectedDirectlyAndCleared()
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        foreach (var overlay in Enum.GetValues<MapOverlay>())
        {
            session.SetOverlay(overlay);
            Assert.Equal(overlay, session.Overlay);
        }
        session.SetOverlay(MapOverlay.Off);
        Assert.Equal(MapOverlay.Off, session.Overlay);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetOverlay((MapOverlay)999));
    }
}
