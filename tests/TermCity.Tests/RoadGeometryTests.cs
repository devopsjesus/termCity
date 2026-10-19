using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RoadGeometryTests
{
    [Theory]
    [InlineData("Dirt Track")]
    [InlineData("Cobbled Road")]
    [InlineData("King's Road")]
    public void NearbyCompletePathsPreserveProspectiveClearanceWithoutDistantRoads(string name)
    {
        var map = new GameMap(96, 96, new GameContent());
        var type = map.Content.Roads.Get(name);
        for (int x = 4; x < 90; x++) map.SetRoad(x, 70, type);
        var cells = CellLines.Between(new(10, 12), new(40, 18));
        var planned = cells.ToDictionary(p => map.Index(p.X, p.Y), _ => type);
        var paths = RoadCurves.Extract(map, (_, _) => false, planned);
        var region = new CellRect(8, 8, 36, 16);
        var nearby = paths.Where(path => RoadCurves.Intersects(path, region, 10)).ToArray();
        Assert.True(nearby.Length < paths.Count);
        var complete = new RoadGeometry(map, paths);
        var local = new RoadGeometry(map, nearby);
        foreach (var cell in region.Cells())
        {
            Assert.Equal(complete.OverlapsCell(cell.X, cell.Y), local.OverlapsCell(cell.X, cell.Y));
            Assert.Equal(complete.RoadAtCellCenter(cell.X, cell.Y)?.Type,
                local.RoadAtCellCenter(cell.X, cell.Y)?.Type);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void LongStraightHighwayLeavesWholeBuildingPlotsClearOnBothSides(int rotation, bool shallow)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 96, MapHeight = 96 });
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        Pos Rotate(Pos cell)
        {
            for (int turn = 0; turn < rotation; turn++) cell = new(95 - cell.Y, cell.X);
            return cell;
        }
        var highway = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        var cells = CellLines.Between(new(4, 20), new(55, 20))
            .Concat(CellLines.Between(new(55, 20), shallow ? new(90, 21) : new(55, 75)));
        foreach (var cell in cells.Select(Rotate)) map.SetRoad(cell.X, cell.Y, highway);
        var paths = RoadCurves.Extract(map, (_, _) => true);
        var layout = new RoadMeshLayout(map, paths);
        var geometry = new RoadGeometry(map, paths);
        game.Touch();
        var woodlot = map.Content.Buildings.Get("Woodlot");
        int roadAxis = rotation switch { 0 => 20, 1 => 75, 2 => shallow ? 75 : 74, _ => 20 };
        bool vertical = rotation % 2 == 1;
        for (int station = 10; station <= (shallow ? 66 : 46); station++)
            foreach (int side in new[] { -1, 1 })
            {
                var anchor = Rotate(new(station, 20));
                int along = vertical ? anchor.Y : anchor.X;
                int across = side < 0 ? roadAxis - 3 : roadAxis + 2;
                var plot = vertical ? new CellRect(across, along, 3, 3) : new CellRect(along, across, 3, 3);
                foreach (var cell in plot.Cells())
                {
                    Assert.False(geometry.OverlapsCell(cell.X, cell.Y),
                        $"Rotation {rotation}, shallow {shallow}: straight-road plot {plot} overlaps at {cell}: " +
                        string.Join("; ", layout.Segments.Where(s => RoadMeshLayout.OverlapsCell(s.Road, cell.X, cell.Y))
                            .Select(s => $"{s.A}->{s.B}: {string.Join(",", s.Road)}")));
                    Assert.DoesNotContain(layout.Segments, s => RoadMeshLayout.OverlapsCell(s.Road, cell.X, cell.Y));
                }
                Assert.True(game.CanPlaceBuilding(woodlot, plot.X, plot.Y),
                    game.BuildingPlacementError(woodlot, plot.X, plot.Y));
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StraightLocalSpokeKeepsItsCellBandWhenJoiningAnAngledHighway(int rotation)
    {
        var map = new GameMap(64, 64, new GameContent());
        var highway = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        var street = map.Content.Roads.Default;
        Pos Rotate(Pos cell)
        {
            for (int turn = 0; turn < rotation; turn++) cell = new(63 - cell.Y, cell.X);
            return cell;
        }
        var trunk = CellLines.Between(new(4, 10), new(54, 24));
        foreach (var cell in trunk.Select(Rotate)) map.SetRoad(cell.X, cell.Y, highway);
        var junction = trunk.Where(p => p.X == 28).OrderByDescending(p => p.Y).First();
        for (int y = junction.Y + 1; y <= 45; y++)
        {
            var cell = Rotate(new(28, y));
            map.SetRoad(cell.X, cell.Y, street);
        }
        var paths = RoadCurves.Extract(map, (_, _) => true);
        var spoke = Assert.Single(paths, p => p.Type == street);
        var anchor = Rotate(new(28, junction.Y + 4));
        bool vertical = rotation % 2 == 0;
        double coordinate = (vertical ? anchor.X : anchor.Y) + 0.5;
        foreach (double value in vertical ? spoke.Xs : spoke.Ys)
            Assert.Equal(coordinate, value / (vertical ? RoadCurves.CellWidth : RoadCurves.CellHeight), 6);
        var geometry = new RoadGeometry(map, paths);
        for (int y = junction.Y + 4; y <= 43; y++)
        {
            var cell = Rotate(new(28, y));
            foreach (int side in new[] { -1, 1 })
            {
                var neighbor = Rotate(new(28 + side, y));
                Assert.False(geometry.OverlapsCell(neighbor.X, neighbor.Y),
                    $"The spoke at {cell} must fill its own cell, not spill into adjacent {neighbor}.");
            }
        }
    }

    [Theory]
    [InlineData("Dirt Track")]
    [InlineData("Cobbled Road")]
    public void RightAngleLocalRoadLeavesBothSidesOfItsStraightLegBuildable(string name)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 64, MapHeight = 64 });
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        var type = map.Content.Roads.Get(name);
        for (int y = 5; y <= 20; y++) map.SetRoad(10, y, type);
        for (int x = 10; x <= 18; x++) map.SetRoad(x, 20, type);
        var highway = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        for (int x = 0; x < map.Width; x++) map.SetRoad(x, 5, highway);
        var paths = RoadCurves.Extract(map, (_, _) => true);
        var layout = new RoadMeshLayout(map, paths);
        var geometry = new RoadGeometry(map, paths);
        game.Touch();
        var well = map.Content.Buildings.Get("Town Well");
        foreach (int x in Enumerable.Range(15, 4))
            foreach (int y in new[] { 19, 21 })
            {
                Assert.False(geometry.OverlapsCell(x, y),
                    $"Straight local-road neighbor {x},{y} blocked by " +
                    string.Join("; ", layout.Segments.Where(s => RoadMeshLayout.OverlapsCell(s.Road, x, y))
                        .Select(s => $"{s.A}->{s.B}: {string.Join(",", s.Road)}")));
                Assert.True(game.CanPlaceBuilding(well, x, y), $"Town Well must be placeable beside the straight leg at {x},{y}.");
            }
    }

    [Fact]
    public void PointerInspectionUsesThePickedRoadSurfaceNotItsOffCurveLogicalOwner()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var type = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        for (int x = 4; x <= 24; x++) map.SetRoad(x, 10, type);
        for (int y = 10; y <= 35; y++) map.SetRoad(24, y, type);
        game.Touch();
        var path = Assert.Single(game.Network.DrawnPaths, p => p.Type == type);
        var layout = new RoadMeshLayout(map, [path]);
        var bend = layout.Segments.OrderBy(s =>
            System.Numerics.Vector2.DistanceSquared((s.A + s.B) / 2, new(24, 11))).First();
        var point = (bend.A + bend.B) / 2;
        var owner = path.SourceCells.Where(p => map.RoadTypeAt(p.X, p.Y) == type).OrderBy(p =>
            System.Numerics.Vector2.DistanceSquared(new(p.X + 0.5f, p.Y + 0.5f), point)).First();
        Assert.Null(game.Network.RoadAtCellCenter(owner.X, owner.Y));
        Assert.DoesNotContain(type.Name, CellInspector.Summary(game, owner));
        Assert.Contains(type.Name, CellInspector.Summary(game, owner, point));
        Assert.DoesNotContain(type.Name, CellInspector.Summary(game, owner, point + new System.Numerics.Vector2(6, -6)));
    }

    [Fact]
    public void VisibleRoadLookupDistinguishesCellCentersFromRoadEdges()
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Default;
        var edge = new RoadPath(type, true, false, [10 * 12.0 - 1, 10 * 12.0 - 1], [8 * 22.0, 13 * 22.0]);
        var geometry = new RoadGeometry(map, [edge]);
        Assert.True(geometry.OverlapsCell(10, 10));
        Assert.Null(geometry.RoadAtCellCenter(10, 10));
        Assert.Null(geometry.RoadAtCellCenter(11, 10));
        Assert.Null(geometry.RoadAtCellCenter(-1, 10));
        var outside = new RoadPath(type, true, false, [10 * 12.0 - 2, 10 * 12.0 - 2], [8 * 22.0, 13 * 22.0]);
        var sideOnly = new RoadGeometry(map, [outside]);
        Assert.True(sideOnly.OverlapsCell(10, 10));
        Assert.Null(sideOnly.RoadAtCellCenter(10, 10));
    }

    [Fact]
    public void InspectorReportsVisibleCurvesEvenOutsideLogicalRoadCells()
    {
        var game = TestCity.Flat();
        for (int x = 0; x < game.Map.Width; x++) game.Map.SetRoad(x, 20, false);
        var highway = game.Map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        for (int x = 4; x <= 24; x++) game.Map.SetRoad(x, 10, highway);
        for (int y = 10; y <= 35; y++) game.Map.SetRoad(24, y, highway);
        game.Touch();
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
        double x = (11 + type.WidthCells / 2.0) * RoadCurves.CellWidth +
            (RoadGeometry.ConnectionTolerance + extra) * RoadCurves.CellWidth;
        double y = (11 + type.WidthCells / 2.0) * RoadCurves.CellHeight +
            (RoadGeometry.ConnectionTolerance + extra) * RoadCurves.CellHeight;
        var path = vertical
            ? new RoadPath(type, true, false, [x, x], [9 * RoadCurves.CellHeight, 12 * RoadCurves.CellHeight])
            : new RoadPath(type, true, false, [9 * RoadCurves.CellWidth, 12 * RoadCurves.CellWidth], [y, y]);
        var geometry = new RoadGeometry(map, [path]);
        Assert.Equal(connected ? type.Rank : 0, geometry.AccessRank(10, 10));
        Assert.False(geometry.OverlapsCell(10, 10));
    }

    [Theory]
    [InlineData("Dirt Track", false)]
    [InlineData("Cobbled Road", false)]
    [InlineData("King's Road", true)]
    public void StraightRoadClearanceMatchesItsActualMeshWidth(string name, bool blocksAdjacent)
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Get(name);
        foreach (bool vertical in new[] { false, true })
        {
            var path = vertical
                ? new RoadPath(type, true, false, [10.5 * 12, 10.5 * 12], [5 * 22.0, 16 * 22.0])
                : new RoadPath(type, true, false, [5 * 12.0, 16 * 12.0], [10.5 * 22, 10.5 * 22]);
            var geometry = new RoadGeometry(map, [path]);
            Assert.True(geometry.OverlapsCell(10, 10));
            Assert.Equal(blocksAdjacent, geometry.OverlapsCell(vertical ? 9 : 10, vertical ? 10 : 9));
            Assert.Equal(blocksAdjacent, geometry.OverlapsCell(vertical ? 11 : 10, vertical ? 10 : 11));
            Assert.False(geometry.OverlapsCell(vertical ? 12 : 10, vertical ? 10 : 12));
        }
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
