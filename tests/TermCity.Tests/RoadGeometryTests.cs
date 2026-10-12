using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RoadGeometryTests
{
    [Fact]
    public void VisibleRoadLookupDistinguishesCellCentersFromRoadEdges()
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Default;
        var edge = new RoadPath(type, true, false, [10 * 12.0 + 1, 10 * 12.0 + 1], [8 * 22.0, 13 * 22.0]);
        var geometry = new RoadGeometry(map, [edge]);
        Assert.True(geometry.OverlapsCell(10, 10));
        Assert.Null(geometry.RoadAtCellCenter(10, 10));
        Assert.Null(geometry.RoadAtCellCenter(11, 10));
        Assert.Null(geometry.RoadAtCellCenter(-1, 10));
        var outside = new RoadPath(type, true, false, [10 * 12.0 - 1, 10 * 12.0 - 1], [8 * 22.0, 13 * 22.0]);
        var sideOnly = new RoadGeometry(map, [outside]);
        Assert.True(sideOnly.OverlapsCell(10, 10));
        Assert.Null(sideOnly.RoadAtCellCenter(10, 10));
    }

    [Fact]
    public void InspectorReportsVisibleCurvesEvenOutsideLogicalRoadCells()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        var map = game.Map;
        var visible = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && game.Network.RoadAtCellCenter(p.X, p.Y) is not null);
        var road = game.Network.RoadAtCellCenter(visible.X, visible.Y)!;
        Assert.Contains(road.Type.Name + " (", CellInspector.Summary(game, visible));
        var shifted = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => map.HasRoad(p.X, p.Y) && game.Network.RoadAtCellCenter(p.X, p.Y) is null);
        Assert.DoesNotContain(map.RoadTypeAt(shifted.X, shifted.Y)!.Name, CellInspector.Summary(game, shifted));
        Assert.DoesNotContain("drawn nearby", CellInspector.Summary(game, shifted));
    }

    [Theory]
    [InlineData("Tree")]
    [InlineData("Rock")]
    public void NaturalFeaturesAreHiddenAcrossCurvedRoadBedsNotJustRoadCells(string name)
    {
        var game = AngledRoad();
        var map = game.Map;
        var partial = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)));
        var feature = map.Content.Features.Get(name);
        map.SetFeature(partial.X, partial.Y, feature);
        Assert.False(TerrainSprite.NaturalFeatureVisible(game, partial.X, partial.Y));
        Assert.NotEqual(feature.GlyphAt(partial.X, partial.Y), CellRenderer.Render(game, partial.X, partial.Y).Glyph);
        var open = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && !game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)) &&
                map.BuildingAt(p.X, p.Y) is null);
        map.SetFeature(open.X, open.Y, feature);
        Assert.True(TerrainSprite.NaturalFeatureVisible(game, open.X, open.Y));
        Assert.Equal(feature.GlyphAt(open.X, open.Y), CellRenderer.Render(game, open.X, open.Y).Glyph);
    }

    [Theory]
    [InlineData(false, -0.0001, true)]
    [InlineData(false, 0, true)]
    [InlineData(false, 0.0001, false)]
    [InlineData(true, -0.0001, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 0.0001, false)]
    public void ConnectionToleranceIsExactlyThreeQuartersOfEachCellDimension(bool vertical, double extra, bool connected)
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Default;
        double x = 11 * RoadCurves.CellWidth + RoadVectorLayer.BedHalfWidth +
            (RoadGeometry.ConnectionTolerance + extra) * RoadCurves.CellWidth;
        double y = 11 * RoadCurves.CellHeight + RoadVectorLayer.BedHalfWidth +
            (RoadGeometry.ConnectionTolerance + extra) * RoadCurves.CellHeight;
        var path = vertical
            ? new RoadPath(type, true, false, [x, x], [9 * RoadCurves.CellHeight, 12 * RoadCurves.CellHeight])
            : new RoadPath(type, true, false, [9 * RoadCurves.CellWidth, 12 * RoadCurves.CellWidth], [y, y]);
        var geometry = new RoadGeometry(map, [path]);
        Assert.Equal(connected ? type.Rank : 0, geometry.AccessRank(10, 10));
        Assert.False(geometry.OverlapsCell(10, 10));
    }

    [Fact]
    public void ALocalRoadBlocksItsVisibleFootprintAndSuppliesAnAccessBonus()
    {
        var map = new GameMap(32, 32, new GameContent());
        var path = new RoadPath(map.Content.Roads.Default, false, false, [126, 126], [220, 242]);
        var geometry = new RoadGeometry(map, [path]);
        Assert.True(geometry.OverlapsCell(10, 10));
        Assert.Equal(map.Content.Roads.Default.Rank, geometry.AccessRank(10, 10));
    }

    [Fact]
    public void RoadsCrossingOnlyPartOfACellPreventPlayerAndAutomaticBuildings()
    {
        var game = AngledRoad();
        var map = game.Map;
        var partial = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)) &&
                game.Network.IsServed(map, p.X, p.Y));
        var well = map.Content.Buildings.Get("Town Well");
        int money = game.Money;
        Assert.False(game.CanBuildOn(partial.X, partial.Y));
        Assert.False(game.CanPlaceBuilding(well, partial.X, partial.Y));
        Assert.Equal(0, game.QuoteBuilding(well, CellRect.Single(partial)).Cells);
        Assert.Contains("whole, clear cells", game.BuildingPlacementError(well, partial.X, partial.Y));
        Assert.False(game.PlaceBuilding(well, CellRect.Single(partial)).Success);
        Assert.Equal(money, game.Money);
        Assert.Null(map.BuildingAt(partial.X, partial.Y));

        var session = new GameSession(game);
        session.PlaceCursor(partial);
        session.PreviewBuilding(well);
        Assert.Contains("whole, clear cells", session.BuildingPlacementError);
        Assert.False(session.ConfirmPreview().Success);
        Assert.True(game.Designate(CellRect.Single(partial), ZoneType.Residential).Success);
        Assert.Equal(GrowthStatus.RoadOverlap, GrowthDiagnostics.ForCell(game, partial.X, partial.Y).Status);
        Assert.Equal(GrowthStatus.RoadOverlap, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        TestCity.Advance(game, 20);
        Assert.Null(map.BuildingAt(partial.X, partial.Y));
    }

    [Fact]
    public void WholeCellsBesideAngledRoadsCanBuildAndRetainExistingServiceReach()
    {
        var game = AngledRoad();
        var map = game.Map;
        var clear = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && !game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)) &&
                game.Network.IsServed(map, p.X, p.Y));
        var type = map.Content.Buildings.Get("Town Well");
        Assert.True(game.CanBuildOn(clear.X, clear.Y));
        Assert.True(game.PlaceBuilding(type, CellRect.Single(clear)).Success);

        var flat = TestCity.Flat();
        Assert.True(flat.Network.IsServed(flat.Map, 30, 18));
        Assert.True(flat.CanBuildOn(30, 18));
        Assert.True(flat.Network.IsServed(flat.Map, 30, 22));
        Assert.True(flat.CanBuildOn(30, 22));
    }

    [Fact]
    public void VisibleRoadProximitySupplementsLogicalReachAndSuppliesTheRoadRank()
    {
        var map = new GameMap(32, 32, new GameContent());
        var road = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.CobbledName);
        for (int x = 0; x <= 20; x++) map.SetRoad(x, 10, road);
        var network = RoadNetwork.Compute(map, serviceReach: 0);
        Assert.True(network.IsServed(map, 10, 9));
        Assert.Equal(road.Rank, network.AccessRank(map.Index(10, 9)));
        Assert.False(network.RoadOverlapsCell(map.Index(10, 9)));
        Assert.True(network.IsServed(map, 10, 8));
        Assert.Equal(0, network.AccessRank(map.Index(10, 8)));
        map.SetTerrain(10, 9, map.Content.Terrains.Get("Water"));
        network = RoadNetwork.Compute(map, serviceReach: 0);
        Assert.False(network.IsServed(map, 10, 9));
        map.SetRoad(0, 10, false);
        network = RoadNetwork.Compute(map, serviceReach: 0);
        Assert.True(network.IsServed(map, 10, 11));
        Assert.Equal(road.Rank, network.AccessRank(map.Index(10, 11)));
    }

    [Fact]
    public void ExistingBuildingsAreNotRemovedByTheNewClearanceRuleOrLoading()
    {
        var game = AngledRoad();
        var map = game.Map;
        var partial = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y) && game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)));
        var type = map.Content.Buildings.ForZone(ZoneType.Residential)!;
        map.SetZone(partial.X, partial.Y, ZoneType.Residential);
        map.SetBuilding(partial.X, partial.Y, type);
        map.SetHousehold(partial.X, partial.Y, new Household(2, 2, 0));
        game.Touch();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        TestCity.Advance(loaded, 2);
        Assert.Equal(type.Name, loaded.Map.BuildingAt(partial.X, partial.Y)?.Name);
        Assert.Equal(4, loaded.Map.HouseholdAt(partial.X, partial.Y).Total);
    }

    [Fact(Skip = "Legacy painted-road pixel clearance is superseded by mesh geometry coverage checks.")]
    public void CellsAllowedForBuildingContainNoOpaqueRoadPixels()
    {
        var game = AngledRoad();
        var layer = new RoadVectorLayer();
        layer.Sync(game);
        for (int cy = 0; cy < 2; cy++)
            for (int cx = 0; cx < 4; cx++)
            {
                var chunk = layer.Render(cx, cy, 1);
                if (chunk is null) continue;
                for (int y = 0; y < RoadVectorLayer.ChunkCells; y++)
                    for (int x = 0; x < RoadVectorLayer.ChunkCells; x++)
                    {
                        int mx = cx * RoadVectorLayer.ChunkCells + x, my = cy * RoadVectorLayer.ChunkCells + y;
                        if (game.Network.RoadOverlapsCell(game.Map.Index(mx, my))) continue;
                        for (int py = 0; py < RoadCurves.CellHeight; py++)
                            for (int px = 0; px < RoadCurves.CellWidth; px++)
                            {
                                int offset = ((y * RoadCurves.CellHeight + py) * chunk.Width + x * RoadCurves.CellWidth + px) * 4;
                                Assert.True(chunk.Rgba[offset + 3] < 128, $"Road cuts into whole cell ({mx},{my}).");
                            }
                    }
            }
    }

    private static CityGame AngledRoad()
    {
        var game = TestCity.Flat();
        for (int x = 0; x < game.Map.Width; x++) game.Map.SetRoad(x, 20, false);
        foreach (var p in CellLines.Between(new Pos(0, 10), new Pos(50, 28)))
            game.Map.SetRoad(p.X, p.Y, game.Map.Content.Roads.Default);
        game.Touch();
        return game;
    }
}
