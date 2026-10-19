using TermCity.Core.Roads;
using TermCity.Core.Session;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class PlacementNavigationTests
{
    [Fact]
    public void PlacementSmokeCanRunIndependentlyWithAGraphicalCapture()
    {
        var options = GodotOptions.Parse(["--placement-smoke-test", "--capture", "/tmp/placement.png"]);
        Assert.True(options.SmokeTest);
        Assert.True(options.BuildingSmokeTest);
        Assert.True(options.PlacementSmokeTest);
        Assert.False(options.PerspectiveSmokeTest);
        Assert.Equal("/tmp/placement.png", options.CapturePath);
        Assert.False(GodotOptions.Parse([]).PlacementSmokeTest);
    }

    [Fact]
    public void BuildingCameraNavigationPreservesThePlotFacingAndQuote()
    {
        var session = new GameSession(TestCity.Flat());
        session.SetViewport(18, 12);
        session.PlaceCursor(new(40, 40));
        session.PreviewBuilding(session.Game.Map.Content.Buildings.Get("Woodlot"));
        session.RotateBuildingPlacement(1);
        var preview = session.Preview!;
        int money = session.Game.Money;
        Assert.True(session.MapNavigationAllowed);
        foreach (int zoom in new[] { -2, -1, 0, 1 })
        {
            session.SetZoom(zoom);
            session.ScrollCamera(3, -2);
            session.CenterOn(new(60, 60));
            Assert.Same(preview, session.Preview);
            Assert.Equal(1, session.Preview!.Rotation);
            Assert.Equal(preview.Area, session.Preview.Area);
            Assert.Equal(preview.Quote, session.Preview.Quote);
            Assert.True(session.BuildingToolActive);
            Assert.True(session.MapNavigationAllowed);
        }
        Assert.Equal(money, session.Game.Money);
        Assert.Empty(session.Game.Map.BuildingFootprints);
    }

    [Fact]
    public void NavigationAllowsLiveToolsButStillBlocksModalConfirmations()
    {
        var session = new GameSession(TestCity.Flat());
        Assert.True(session.MapNavigationAllowed);
        session.PlaceCursor(new(40, 40));
        session.PreviewRoadTemplate(RoadTemplate.TrafficCircle);
        Assert.True(session.MapNavigationAllowed);
        session.CancelPreview();
        session.BeginRoadLine();
        Assert.True(session.MapNavigationAllowed);
        session.CancelPreview();
        session.PreviewDemolish();
        Assert.False(session.MapNavigationAllowed);
        session.CancelPreview();
        session.ShowAreaMenu();
        Assert.False(session.MapNavigationAllowed);
        session.ClosePrompt();
        Assert.True(session.MapNavigationAllowed);
    }

    [Fact]
    public void OverlaysAndEdgeScrollingDoNotCancelBuildingPlacement()
    {
        var session = new GameSession(TestCity.Flat());
        session.PlaceCursor(new(40, 40));
        session.PreviewBuilding(session.Game.Map.Content.Buildings.Get("Town Well"));
        var preview = session.Preview;
        session.CycleOverlay();
        session.ToggleEdgeScroll();
        Assert.Same(preview, session.Preview);
        Assert.True(session.MapNavigationAllowed);
        Assert.True(session.EdgeScrollEnabled);
        Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(40, 40));
    }
}
