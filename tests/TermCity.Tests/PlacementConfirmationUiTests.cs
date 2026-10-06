using System.Drawing;
using Terminal.Gui.Input;
using TermCity.App.Views;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

[Collection("UI")]
public class PlacementConfirmationUiTests
{
    private static GameSession Session()
    {
        var session = new GameSession(TestCity.Flat());
        session.Game.Paused = true;
        return session;
    }

    [Fact]
    public async Task DemolitionPopupIsNearTargetAndNoPreservesTheCell()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            var cell = session.ScreenToMap(30, 10);
            await ui.Mouse(MouseFlags.LeftButtonPressed, 30, 11);
            await ui.Mouse(MouseFlags.LeftButtonReleased, 30, 11);
            session.Game.Designate(CellRect.Single(cell), ZoneType.Residential);
            await ui.Press(Key.D);
            string screen = await ui.Screen();
            Assert.Contains("Demolish here?", screen);
            Assert.Contains("[Yes]", screen);
            Assert.Contains("[No]", screen);
            Assert.DoesNotContain("[Confirm]", screen);
            var popup = ui.Window.SubViews.OfType<PlacementConfirmationView>().Single();
            var map = ui.Window.SubViews.OfType<MapView>().Single();
            Assert.True(map.Frame.Contains(popup.Frame));
            Assert.False(popup.Frame.Contains(new Point(30, 11)));
            Assert.InRange(Math.Abs(popup.Frame.Y - 11), 0, 7);
            var preview = session.Preview;
            await ui.Mouse(MouseFlags.LeftButtonPressed, 85, 28);
            await ui.Mouse(MouseFlags.LeftButtonReleased, 85, 28);
            Assert.Equal(cell, session.Cursor);
            Assert.Same(preview, session.Preview);
            await ui.Press(Key.CursorRight);
            await ui.Press(Key.Enter);
            Assert.Null(session.Preview);
            Assert.Equal(ZoneType.Residential, session.Game.Map.ZoneAt(cell.X, cell.Y));
            await ui.Press(Key.D);
            await ui.Press(Key.Y);
            Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(cell.X, cell.Y));
            Assert.False(popup.Visible);
        });
    }

    [Theory]
    [InlineData(-2, 0, 0)]
    [InlineData(-1, 85, 27)]
    [InlineData(0, 0, 27)]
    [InlineData(1, 85, 0)]
    public async Task PopupStaysOnMapAtEdgesAndZoomLevels(int zoom, int x, int y)
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            session.SetZoom(zoom);
            await ui.Mouse(MouseFlags.LeftButtonPressed, x, y + 1);
            await ui.Mouse(MouseFlags.LeftButtonReleased, x, y + 1);
            await ui.Press(Key.D);
            await ui.Screen();
            var popup = ui.Window.SubViews.OfType<PlacementConfirmationView>().Single();
            var map = ui.Window.SubViews.OfType<MapView>().Single();
            Assert.True(map.Frame.Contains(popup.Frame), $"{map.Frame}: {popup.Frame}");
            Assert.False(popup.Frame.Contains(new Point(x, y + 1)));
            await ui.Press(Key.N);
            Assert.Null(session.Preview);
        });
    }

    [Fact]
    public async Task FailedConfirmationShowsErrorKeepsPopupAndDoesNotSpend()
    {
        var session = Session();
        session.Game.Money = 1;
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.B);
            await ui.Press(Key.Enter);
            Assert.Contains("Not enough money", await ui.Screen());
            Assert.NotNull(session.Preview);
            Assert.True(ui.Window.SubViews.OfType<PlacementConfirmationView>().Single().Visible);
            Assert.Equal(1, session.Game.Money);
            await ui.Press(Key.N);
            Assert.Null(session.Preview);
        });
    }

    [Fact]
    public async Task UDezonesImmediatelyAndOriginalZoneKeyCancelsBuildingRemoval()
    {
        var session = Session();
        session.PlaceCursor(new Pos(80, 40));
        var map = session.Game.Map;
        session.Game.Designate(CellRect.Single(session.Cursor), ZoneType.Residential);
        map.SetBuilding(80, 40, map.Content.Buildings.ForZone(ZoneType.Residential)!);
        map.SetHousehold(80, 40, new Household(2, 2, 0));
        session.Game.Touch();
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.U);
            Assert.Equal(ZoneType.None, map.ZoneAt(80, 40));
            Assert.NotNull(map.BuildingAt(80, 40));
            Assert.NotNull(map.ZoneRemovalAt(80, 40));
            Assert.Null(session.Preview);
            Assert.Equal(4, session.Game.Stats.Population);
            Assert.Contains("2-3 game weeks", await ui.Screen());
            await ui.Press(Key.R);
            Assert.Equal(ZoneType.Residential, map.ZoneAt(80, 40));
            Assert.Null(map.ZoneRemovalAt(80, 40));
            Assert.NotNull(map.BuildingAt(80, 40));
        });
    }

    [Fact]
    public async Task AreaMenuCanDezoneASelection()
    {
        var session = Session();
        session.PlaceCursor(new Pos(80, 40));
        session.Game.Designate(CellRect.Single(session.Cursor), ZoneType.Residential);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.Enter);
            await ui.Press(Key.A);
            await ui.Press(Key.U);
            await ui.Press(Key.Enter);
            Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(80, 40));
            Assert.Null(session.Preview);
        });
    }

    [Fact]
    public async Task PreviewReleasesAnActiveDragSoMouseCanChooseNo()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(MouseFlags.LeftButtonPressed | MouseFlags.Shift, 20, 10);
            await ui.Press(Key.D);
            await ui.Screen();
            var popup = ui.Window.SubViews.OfType<PlacementConfirmationView>().Single();
            await ui.Mouse(MouseFlags.LeftButtonReleased, 20, 10);
            await ui.Mouse(MouseFlags.LeftButtonPressed, popup.Frame.X + 12, popup.Frame.Y + 4);
            await ui.Mouse(MouseFlags.LeftButtonReleased, popup.Frame.X + 12, popup.Frame.Y + 4);
            Assert.Null(session.Preview);
            Assert.False(popup.Visible);
        });
    }
}
