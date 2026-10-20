using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class NearestRoadFootpathTests
{
    private static void Home(GameMap map, int x, int y)
    {
        map.SetZone(x, y, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(x, y, 1, 1));
    }

    [Fact]
    public void HomesAlongARoadWearOnlyTheShortestRoadAccessNotASidewalkBetweenHomes()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        Home(map, 10, 17);
        Home(map, 18, 17);
        for (int day = 0; day < 10; day++) TravelRoutes.WearPaths(map);
        Assert.All(Enumerable.Range(11, 7), x => Assert.Equal(0, map.PathWearAt(x, 17)));
        foreach (int x in new[] { 10, 18 })
            foreach (int y in new[] { 18, 19 })
                Assert.True(map.PathWearAt(x, y) > 0);
        Assert.Equal(4, map.PathLayer.Count(wear => wear > 0));
        Assert.All(TravelRoutes.FootpathJourneys(map), journey =>
            Assert.True(TravelRoutes.IsPathRoadTarget(map, journey[^1])));
    }

    [Fact]
    public void ResidentialNeighborsDoNotCreatePathsBetweenThemselvesWithoutRoads()
    {
        var map = TestCity.Flat().Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        Home(map, 10, 10);
        Home(map, 18, 10);
        for (int day = 0; day < 12; day++) Assert.False(TravelRoutes.WearPaths(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
        Assert.Empty(TravelRoutes.FootpathJourneys(map));
    }

    [Fact]
    public void VisibleHighwayWidthIsTheRoadTargetNotItsOffsetLogicalCenterline()
    {
        var map = TestCity.Flat().Map;
        var highway = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        for (int x = 0; x < map.Width; x++) map.SetRoad(x, 20, highway);
        Home(map, 10, 22);
        Home(map, 18, 23);
        for (int day = 0; day < 10; day++) TravelRoutes.WearPaths(map);
        Assert.Equal(0, map.PathWearAt(10, 21));
        Assert.Equal(0, map.PathWearAt(18, 21));
        Assert.Equal(1, map.PathLayer.Count(wear => wear > 0));
        Assert.True(map.PathWearAt(18, 22) > 0);
        var stroke = Assert.Single(FootpathLayout.Strokes(map));
        Assert.True(stroke.EndsAtRoad);
        var layout = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => true));
        Assert.True(layout.InRoadInterior(FootpathLayout.RibbonSections(stroke, layout)[^1].Center));
    }

    [Fact]
    public void ShortestApproachStartsFromTheNearestEdgeOfAMultiCellBuilding()
    {
        var map = TestCity.Flat().Map;
        var area = new CellRect(20, 12, 2, 2);
        foreach (var cell in area.Cells()) map.SetZone(cell.X, cell.Y, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), area);
        for (int day = 0; day < 14; day++) TravelRoutes.WearPaths(map);
        var journey = Assert.Single(TravelRoutes.FootpathJourneys(map));
        Assert.Equal(8, journey.Length);
        Assert.Equal(13, map.PosOf(journey[0]).Y);
        Assert.All(journey.Skip(1).SkipLast(1), cell => Assert.Equal(20, map.PosOf(cell).X));
        Assert.Equal(6, map.PathLayer.Count(wear => wear > 0));
    }

    [Fact]
    public void RoadReachableWorkplacesDoNotWearParallelBuildingLinksEither()
    {
        var map = TestCity.Flat().Map;
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(10, 17, 1, 1));
        map.SetBuildingFootprint(well, new(18, 17, 1, 1));
        for (int day = 0; day < 10; day++) TravelRoutes.WearPaths(map);
        Assert.All(Enumerable.Range(11, 7), x => Assert.Equal(0, map.PathWearAt(x, 17)));
        Assert.Equal(4, map.PathLayer.Count(wear => wear > 0));
    }

    [Fact]
    public void SavedResidentialLinksAndRoadsideWearAreNotRenderedAsNewParallelTrails()
    {
        var map = TestCity.Flat().Map;
        Home(map, 10, 17);
        Home(map, 18, 17);
        for (int x = 11; x < 18; x++) map.PathLayer[map.Index(x, 17)] = 6;
        Assert.Empty(FootpathLayout.Strokes(map));
        Assert.All(Enumerable.Range(11, 7), x => Assert.Equal(6, map.PathWearAt(x, 17)));
        var roadless = TestCity.Flat().Map;
        foreach (int cell in roadless.RoadCells.ToArray()) roadless.SetRoad(cell % roadless.Width, cell / roadless.Width, false);
        Home(roadless, 10, 10);
        Home(roadless, 18, 10);
        for (int x = 11; x < 18; x++) roadless.PathLayer[roadless.Index(x, 10)] = 6;
        Assert.Empty(FootpathLayout.Strokes(roadless));
    }
}
