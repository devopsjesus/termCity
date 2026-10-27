using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class AreaOverlayVisibilityTests
{
    [Fact]
    public void OrganicSettlementVacanciesDoNotBecomeZonedAreaMarkers()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 72 }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var bounds = game.Settlement!.Bounds;
        var vacant = game.VacantPlots(ZoneType.Residential).ToArray();
        var session = new GameSession(game);
        string before = SaveGameStore.Serialize(game);
        session.SetAreaOverlays(true);
        Assert.NotEmpty(vacant);
        Assert.All(vacant, plot => Assert.All(plot.Cells(), cell =>
        {
            Assert.True(bounds.Contains(cell));
            Assert.Equal(ZoneType.None, game.Map.ZoneAt(cell.X, cell.Y));
        }));
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void AreasStartHiddenAndOnlyRealVisibilityChangesNotify()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game);
        session.SetOverlay(MapOverlay.Fire);
        string before = SaveGameStore.Serialize(game);
        int version = game.MapVersion, changes = 0;
        session.Changed += () => changes++;

        Assert.False(session.AreaOverlaysVisible);
        session.SetAreaOverlays(false);
        Assert.Equal(0, changes);
        session.ToggleAreaOverlays();
        Assert.True(session.AreaOverlaysVisible);
        session.SetAreaOverlays(true);
        Assert.Equal(1, changes);
        session.ToggleAreaOverlays();
        Assert.False(session.AreaOverlaysVisible);
        Assert.Equal(2, changes);
        Assert.Equal(MapOverlay.Fire, session.Overlay);
        Assert.Equal(version, game.MapVersion);
        Assert.Equal(before, SaveGameStore.Serialize(game));
        Assert.Equal(0, session.UndoCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewGameRetainsAreaVisibility(bool visible)
    {
        var session = new GameSession(TestCity.Flat());
        session.SetAreaOverlays(visible);
        session.NewGame(session.Game.Config with { Seed = 42 });
        Assert.Equal(visible, session.AreaOverlaysVisible);
    }

    [Fact]
    public void HiddenVacantZonesUseTerrainWithoutDisablingServiceTint()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 10, ZoneType.Residential);
        var terrain = game.Map.TerrainAt(10, 10);
        var hidden = CellRenderer.Render(game, 10, 10, showAreas: false);
        Assert.Equal(new CellVisual(terrain.GlyphAt(10, 10), terrain.Foreground, terrain.Background), hidden);
        Assert.NotEqual(hidden, CellRenderer.Render(game, 10, 10));
        var tinted = CellRenderer.Render(game, 10, 10, MapOverlay.Fire, showAreas: false);
        Assert.Equal(MapOverlays.Tint(game, MapOverlay.Fire, 10, 10, hidden.Background), tinted.Background);
        Assert.NotEqual(hidden.Background, tinted.Background);
    }

    [Fact]
    public void HidingAreasPreservesBuildingsRoadsAndFootpaths()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var home = map.Content.Buildings.ForZone(ZoneType.Residential)!;
        map.SetZone(10, 10, ZoneType.Residential);
        map.SetBuilding(10, 10, home);
        map.SetPathWear(12, 10, 6);
        map.SetPathWear(13, 10, 6);
        game.Touch();
        string before = SaveGameStore.Serialize(game);

        var shownHome = CellRenderer.Render(game, 10, 10);
        var hiddenHome = CellRenderer.Render(game, 10, 10, showAreas: false);
        Assert.Equal(shownHome.Glyph, hiddenHome.Glyph);
        Assert.Equal(shownHome.Foreground, hiddenHome.Foreground);
        foreach (var cell in new[] { new Pos(12, 10), new Pos(13, 10), new Pos(10, 20) })
            Assert.Equal(CellRenderer.Render(game, cell.X, cell.Y),
                CellRenderer.Render(game, cell.X, cell.Y, showAreas: false));
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void TerminalGridResamplesHiddenZoningAtEveryOverheadZoom(int zoom)
    {
        var game = TestCity.Flat();
        game.Map.SetZone(10, 10, ZoneType.Residential);
        game.Touch();
        var session = new GameSession(game);
        session.SetZoom(zoom);
        var grid = new TerminalGrid();
        grid.Resize(120, 110);
        grid.Fill(session);
        session.CenterOn(new(10, 10));
        string before = SaveGameStore.Serialize(game);
        grid.Fill(session);
        var hidden = Snapshot(grid, session);
        session.ToggleAreaOverlays();
        grid.Fill(session);
        var shown = Snapshot(grid, session);
        Assert.NotEqual(hidden, shown);
        session.ToggleAreaOverlays();
        grid.Fill(session);
        Assert.Equal(hidden, Snapshot(grid, session));
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void MinimapInvalidatesVisibilityWithoutMapMutationAndKeepsBuildingsAndRoads()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        map.SetZone(10, 10, ZoneType.Residential);
        map.SetZone(12, 10, ZoneType.Residential);
        map.SetBuilding(12, 10, map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Touch();
        int version = game.MapVersion;
        string before = SaveGameStore.Serialize(game);
        var image = new MinimapImage();
        image.Update(game, map.Width, map.Height, showAreas: false);
        var hidden = image[10, 10];
        var building = image[12, 10];
        var road = image[10, 20];
        Assert.Equal(image[9, 10], hidden);
        Assert.NotEqual(hidden, building);
        Assert.NotEqual(hidden, road);

        image.Update(game, map.Width, map.Height, showAreas: true);
        Assert.NotEqual(hidden, image[10, 10]);
        Assert.Equal(building, image[12, 10]);
        Assert.Equal(road, image[10, 20]);
        image.Update(game, map.Width, map.Height, showAreas: false);
        Assert.Equal(hidden, image[10, 10]);
        Assert.Equal(version, game.MapVersion);
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    private static CellVisual?[] Snapshot(TerminalGrid grid, GameSession session) =>
        Enumerable.Range(0, grid.Rows).SelectMany(y =>
            Enumerable.Range(0, grid.Columns).Select(x => grid.VisualAt(session, x, y))).ToArray();
}
