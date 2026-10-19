using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class HillFootpathTests
{
    [Fact]
    public void GeneratedPathsGoAroundAHillInsteadOfClimbingItsTerraces()
    {
        var map = TestCity.Flat().Map;
        Home(map, 10, 15);
        foreach (var cell in new CellRect(8, 17, 5, 2).Cells())
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Hill"));
        for (int day = 0; day < 35; day++) TravelRoutes.WearPaths(map);
        var journey = Assert.Single(TravelRoutes.FootpathJourneys(map));
        Assert.Contains(journey, cell => cell % map.Width < 8 || cell % map.Width > 12);
        Assert.All(journey, cell => Assert.Equal(0, TerrainSprite.Elevation(map, cell % map.Width, cell / map.Width)));
        Assert.DoesNotContain(Enumerable.Range(0, map.PathLayer.Length),
            cell => map.PathLayer[cell] > 0 && TerrainSprite.Elevation(map, cell % map.Width, cell / map.Width) > 0);
        Assert.NotEmpty(FootpathLayout.Strokes(map));
    }

    [Fact]
    public void IsolatedTerraceDoesNotGrowAPathDownItsCliff()
    {
        var map = TestCity.Flat().Map;
        foreach (var cell in new CellRect(9, 14, 3, 3).Cells())
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Hill"));
        Home(map, 10, 15);
        for (int day = 0; day < 20; day++) Assert.False(TravelRoutes.WearPaths(map));
        Assert.Empty(TravelRoutes.FootpathJourneys(map));
        Assert.Empty(FootpathLayout.Strokes(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
    }

    [Fact]
    public void LevelTerraceTopsDoNotGrowNaturalPaths()
    {
        var map = TestCity.Flat().Map;
        foreach (var cell in new CellRect(4, 12, 15, 11).Cells())
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Hill"));
        map.HillLevels = Enumerable.Repeat((byte)1, map.Width * map.Height).ToArray();
        Home(map, 10, 15);
        for (int day = 0; day < 20; day++) TravelRoutes.WearPaths(map);
        Assert.Empty(TravelRoutes.FootpathJourneys(map));
        Assert.Empty(FootpathLayout.Strokes(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
        Assert.True(double.IsFinite(TravelRoutes.Compute(map, [map.Index(10, 15)]).Cost[map.Index(10, 20)]));
    }

    [Fact]
    public void EvenCollapsedHillCellsAreNotFootpathShortcuts()
    {
        var map = TestCity.Flat().Map;
        foreach (int road in map.RoadCells.ToArray()) map.SetRoad(road % map.Width, road / map.Width, false);
        for (int y = 0; y < map.Height; y++) map.SetTerrain(11, y, map.Content.Terrains.Get("Hill"));
        map.CoastalHillCaps = Enumerable.Repeat((byte)2, map.Width * map.Height).ToArray();
        Assert.Equal(0, TerrainSprite.Elevation(map, 11, 12));
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(8, 12, 1, 1));
        map.SetBuildingFootprint(well, new(14, 12, 1, 1));
        for (int day = 0; day < 20; day++) Assert.False(TravelRoutes.WearPaths(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
        Assert.False(TravelRoutes.CanFootpathStep(map, map.Index(10, 12), map.Index(11, 12)));
        Assert.True(double.IsFinite(TravelRoutes.Compute(map, [map.Index(8, 12)]).Cost[map.Index(14, 12)]));
    }

    [Fact]
    public void SavedWearAndFlaredRoadConnectionsNeverPaintOverHillCells()
    {
        var map = TestCity.Flat().Map;
        foreach (int road in map.RoadCells.ToArray()) map.SetRoad(road % map.Width, road / map.Width, false);
        for (int y = 0; y < map.Height; y++)
        {
            map.SetTerrain(11, y, map.Content.Terrains.Get("Hill"));
            map.SetRoad(11, y, true);
        }
        map.CoastalHillCaps = Enumerable.Repeat((byte)2, map.Width * map.Height).ToArray();
        Home(map, 8, 12);
        for (int x = 9; x <= 12; x++) map.SetPathWear(x, 12, 6);
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => true));
        var paths = new FootpathSurfaceLayout(map, roads);
        Assert.Contains(paths.Surfaces, surface => surface.Polygons.Count > 0);
        for (int y = 0; y < map.Height; y++)
            Assert.All(paths.AtCell(11, y), polygon => Assert.False(RoadMeshLayout.OverlapsCell(polygon, 11, y)));
        Assert.Equal(6, map.PathWearAt(11, 12));
        Assert.Contains(FootpathLayout.Strokes(map), stroke => stroke.EndsAtRoad);
    }

    [Theory]
    [InlineData(11, 12)]
    [InlineData(512, 307)]
    public void PathBoundaryHeightSamplesStayInsideTheLowerSide(int x, int y)
    {
        var boundary = new System.Numerics.Vector2(x, y + 0.5f);
        var sample = FootpathSurfaceLayout.TerrainSample(boundary, new(x - 0.08f, y + 0.48f));
        Assert.True(sample.X < x);
        Assert.InRange(x - sample.X, 0.0005f, 0.0011f);
        Assert.Equal(x - 1, (int)MathF.Floor(sample.X));
        Assert.Equal(new System.Numerics.Vector2(x, y + 0.5f), boundary);
        Assert.Equal(boundary, FootpathSurfaceLayout.TerrainSample(boundary, boundary));
    }

    [Fact]
    public void LegacyWearDoesNotReconnectAcrossACliff()
    {
        var map = TestCity.Flat().Map;
        foreach (int road in map.RoadCells.ToArray()) map.SetRoad(road % map.Width, road / map.Width, false);
        for (int y = 0; y < map.Height; y++) map.SetTerrain(11, y, map.Content.Terrains.Get("Hill"));
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(8, 12, 1, 1));
        map.SetBuildingFootprint(well, new(14, 12, 1, 1));
        for (int x = 9; x < 14; x++) map.PathLayer[map.Index(x, 12)] = 6;
        Assert.False(TravelRoutes.CanFootpathStep(map, map.Index(10, 12), map.Index(11, 12)));
        Assert.False(TravelRoutes.CanFootpathStep(map, map.Index(11, 12), map.Index(12, 12)));
        var strokes = FootpathLayout.Strokes(map);
        Assert.NotEmpty(strokes);
        Assert.All(strokes, stroke =>
        {
            var heights = stroke.Points.Select(point =>
                TerrainSprite.Elevation(map, (int)Math.Floor(point.X), (int)Math.Floor(point.Y))).ToArray();
            Assert.InRange(heights.Max() - heights.Min(), 0, TravelRoutes.MaximumFootpathStep);
        });
        Assert.Equal(0, FootpathLayout.Connections(map, 11, 12));
        Assert.All(Enumerable.Range(9, 5), x => Assert.Equal(6, map.PathWearAt(x, 12)));
    }

    [Fact]
    public void ServiceTravelAndRoadConnectivityRemainIndependentOfFootpathArtwork()
    {
        var map = TestCity.Flat().Map;
        foreach (var cell in new CellRect(9, 14, 3, 3).Cells())
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Hill"));
        var routes = TravelRoutes.Compute(map, [map.Index(10, 15)]);
        Assert.True(double.IsFinite(routes.Cost[map.Index(10, 20)]));
    }

    [Fact]
    public void PathWearEditingValidatesBoundsAndPreservesTheWearLimit()
    {
        var map = TestCity.Flat().Map;
        map.SetPathWear(10, 10, 12);
        Assert.Equal(12, map.PathWearAt(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetPathWear(-1, 10, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetPathWear(10, map.Height, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetPathWear(10, 10, 13));
    }

    private static void Home(GameMap map, int x, int y)
    {
        map.SetZone(x, y, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(x, y, 1, 1));
    }
}
