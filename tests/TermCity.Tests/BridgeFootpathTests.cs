using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class BridgeFootpathTests
{
    private static GameMap RiverBridge(string roadName = DefaultRoads.KingsRoadName)
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 40, MapHeight = 32 }).Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 18; x <= 20; x++)
                map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        foreach (int index in map.RoadCells.ToArray())
            map.SetRoad(index % map.Width, index / map.Width, map.Content.Roads.Get(roadName));
        return map;
    }

    [Theory]
    [InlineData(DefaultRoads.TrackName)]
    [InlineData(DefaultRoads.KingsRoadName)]
    public void NeitherLogicalBridgeCellsNorTheirVisibleOuterBandsArePathTargets(string roadName)
    {
        var map = RiverBridge(roadName);
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => true));
        Assert.Contains(roads.Segments, segment => segment.Bridge);
        int checkedCells = 0;
        foreach (var bridge in roads.Segments.Where(segment => segment.Bridge))
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    if (!RoadMeshLayout.OverlapsCell(bridge.Corridor, x, y)) continue;
                    int cell = map.Index(x, y);
                    Assert.True(TravelRoutes.IsFootpathBridgeCell(map, cell));
                    Assert.False(TravelRoutes.IsPathRoadTarget(map, cell));
                    checkedCells++;
                }
        Assert.True(checkedCells > 0);
        Assert.True(TravelRoutes.IsPathRoadTarget(map, map.Index(5, 20)));
    }

    [Fact]
    public void NewTrailsChooseOrdinaryRoadApproachesInsteadOfBridgeSides()
    {
        var map = RiverBridge();
        foreach (int x in new[] { 15, 24 })
        {
            map.SetZone(x, 17, ZoneType.Residential);
            map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(x, 17, 1, 1));
        }
        for (int day = 0; day < 16; day++) TravelRoutes.WearPaths(map);
        var journeys = TravelRoutes.FootpathJourneys(map).ToArray();
        Assert.Equal(2, journeys.Length);
        Assert.All(journeys, journey =>
        {
            Assert.True(TravelRoutes.IsPathRoadTarget(map, journey[^1]));
            Assert.All(journey, cell => Assert.False(TravelRoutes.IsFootpathBridgeCell(map, cell)));
        });
        Assert.Contains(map.PathLayer, wear => wear > 0);
        Assert.All(Enumerable.Range(0, map.PathLayer.Length).Where(cell => TravelRoutes.IsFootpathBridgeCell(map, cell)),
            cell => Assert.Equal(0, map.PathLayer[cell]));
    }

    [Fact]
    public void BridgeOnlyRoadsDoNotAttractPaths()
    {
        var map = RiverBridge();
        foreach (int index in map.RoadCells.ToArray())
            if (index % map.Width < 18 || index % map.Width > 20)
                map.SetRoad(index % map.Width, index / map.Width, false);
        map.SetZone(16, 17, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(16, 17, 1, 1));
        for (int day = 0; day < 12; day++) Assert.False(TravelRoutes.WearPaths(map));
        Assert.Empty(TravelRoutes.FootpathJourneys(map));
        Assert.Empty(FootpathLayout.Strokes(map));
    }

    [Fact]
    public void GeneralTravelStillCrossesTheRiverOnItsBridge()
    {
        var map = RiverBridge();
        var routes = TravelRoutes.Compute(map, [map.Index(5, 20)]);
        Assert.True(double.IsFinite(routes.Cost[map.Index(35, 20)]));
        Assert.True(TravelRoutes.Walkable(map, map.Index(19, 20)));
        Assert.False(TravelRoutes.IsPathRoadTarget(map, map.Index(19, 20)));
    }

    [Fact]
    public void RaisedBridgesOverBuildableValleysAreNotPathTargetsEither()
    {
        var map = new GameMap(48, 32, new GameContent());
        for (int y = 5; y < 25; y++)
            for (int x = 4; x < 35; x++)
                if (x < 17 || x > 20) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        for (int x = 8; x <= 31; x++) map.SetRoad(x, 15, true);
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => false));
        Assert.Contains(roads.Segments, segment => segment.Bridge);
        Assert.True(map.TerrainAt(19, 15).Buildable);
        Assert.True(TravelRoutes.IsFootpathBridgeCell(map, map.Index(19, 15)));
        Assert.False(TravelRoutes.IsPathRoadTarget(map, map.Index(19, 15)));
        Assert.True(double.IsFinite(TravelRoutes.Compute(map, [map.Index(8, 15)]).Cost[map.Index(31, 15)]));
    }

    [Fact]
    public void BridgeClassificationUpdatesWhenTheTerrainChanges()
    {
        var map = RiverBridge();
        Assert.True(TravelRoutes.IsFootpathBridgeCell(map, map.Index(19, 20)));
        foreach (int y in Enumerable.Range(0, map.Height))
            for (int x = 18; x <= 20; x++) map.SetTerrain(x, y, map.Content.Terrains.Base);
        Assert.Null(map.FootpathBridgeCells);
        Assert.False(TravelRoutes.IsFootpathBridgeCell(map, map.Index(19, 20)));
        Assert.True(TravelRoutes.IsPathRoadTarget(map, map.Index(19, 20)));
    }

    [Fact]
    public void RibbonAndCapGeometryCannotPaintOntoBridgeDecksOrParapets()
    {
        var map = RiverBridge();
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => true));
        var stroke = new FootpathLayout.Stroke([new(14.5f, 18.5f), new(19.5f, 20.5f)], 6, false, true);
        var surfaces = new FootpathSurfaceLayout([stroke], roads);
        Assert.NotEmpty(surfaces.Surfaces.SelectMany(surface => surface.Polygons));
        foreach (var polygon in surfaces.Surfaces.SelectMany(surface => surface.Polygons))
            foreach (var bridge in roads.Segments.Where(segment => segment.Bridge))
                for (int vertex = 1; vertex < polygon.Length - 1; vertex++)
                    Assert.InRange(ZoneSurfaceLayoutTests.IntersectionArea(
                        [polygon[0], polygon[vertex], polygon[vertex + 1]], bridge.Corridor), 0, 0.000002);
    }

    [Fact]
    public void SavedBridgeWearIsRetainedButNeverRenderedAsFootpaths()
    {
        var map = RiverBridge();
        for (int x = 0; x < map.Width; x++) map.SetPathWear(x, 20, 6);
        Assert.Empty(FootpathLayout.Strokes(map));
        Assert.All(Enumerable.Range(0, map.Width), x => Assert.Equal(6, map.PathWearAt(x, 20)));
        Assert.False(TravelRoutes.CanFootpathStep(map, map.Index(19, 20), map.Index(20, 20)));
    }
}
