using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ZoneSurfaceLayoutTests
{
    [Theory]
    [InlineData(17, 23)]
    [InlineData(511, 307)]
    public void EveryCheckerTriangleFollowsItsOwningTerrainAndCoversItsEntireTop(int x, int y)
    {
        var triangles = ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, x, y),
            Plane(x, y, x + 1, y + 1, 0.72f));
        Assert.NotEmpty(triangles);
        Assert.Equal(1, triangles.Sum(triangle => Area(Project(triangle))), 5);
        Assert.All(triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C }), point =>
            Assert.Equal(0.72f + ZoneSurfaceLayout.SurfaceLift, point.Y, 5));
    }

    [Fact]
    public void LowerAndUpperTerraceEdgesHaveSeparateSamplingHeightsWithoutMovingTheirBoundaries()
    {
        var lower = ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 10, 10), Plane(10, 10, 11, 11, 0));
        var upper = ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 11, 10), Plane(11, 10, 12, 11, 0.72f));
        var lowEdge = lower.SelectMany(face => new[] { face.A, face.B, face.C }).Where(point => point.X == 11).ToArray();
        var highEdge = upper.SelectMany(face => new[] { face.A, face.B, face.C }).Where(point => point.X == 11).ToArray();
        Assert.NotEmpty(lowEdge);
        Assert.NotEmpty(highEdge);
        Assert.All(lowEdge, point => Assert.Equal(ZoneSurfaceLayout.SurfaceLift, point.Y));
        Assert.All(highEdge, point => Assert.Equal(0.72f + ZoneSurfaceLayout.SurfaceLift, point.Y));
    }

    [Fact]
    public void RoadCutHeightBreaksCannotPullTheHilltopCheckerBelowItsSurface()
    {
        Vector3[] terrain = [.. Plane(0, 0, 0.22f, 1, 0.05f), .. Plane(0.22f, 0, 1, 1, 0.72f),
            new(0.22f, 0.05f, 0), new(0.22f, 0.72f, 0), new(0.22f, 0.72f, 1),
            new(0.22f, 0.05f, 0), new(0.22f, 0.72f, 1), new(0.22f, 0.05f, 1)];
        var triangles = ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 0, 0), terrain);
        Assert.Equal(1, triangles.Sum(triangle => Area(Project(triangle))), 5);
        double plateauArea = 0;
        foreach (var triangle in triangles)
        {
            float center = (triangle.A.X + triangle.B.X + triangle.C.X) / 3;
            float height = (center < 0.22f ? 0.05f : 0.72f) + ZoneSurfaceLayout.SurfaceLift;
            Assert.All(new[] { triangle.A, triangle.B, triangle.C }, point => Assert.Equal(height, point.Y, 5));
            if (center > 0.22f) plateauArea += Area(Project(triangle));
        }
        Assert.Equal(0.78, plateauArea, 5);
    }

    [Fact]
    public void NonPlanarTerrainIsFollowedAtVerticesEdgesAndTriangleInteriors()
    {
        Vector3 center = new(0.37f, 0.7f, 0.41f);
        Vector3[] corners = [new(0, 0.2f, 0), new(0, 0.2f, 1), new(1, 0.2f, 1), new(1, 0.2f, 0)];
        var terrain = Enumerable.Range(0, 4).SelectMany(index =>
            new[] { corners[index], corners[(index + 1) % 4], center }).ToArray();
        var triangles = ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 0, 0), terrain);
        Assert.Equal(1, triangles.Sum(triangle => Area(Project(triangle))), 5);
        foreach (var triangle in triangles)
            foreach (var point in new[] { triangle.A, triangle.B, triangle.C, (triangle.A + triangle.B) / 2,
                (triangle.B + triangle.C) / 2, (triangle.C + triangle.A) / 2, (triangle.A + triangle.B + triangle.C) / 3 })
            {
                float expected = float.NegativeInfinity;
                for (int face = 0; face < terrain.Length; face += 3)
                {
                    var a = terrain[face]; var b = terrain[face + 1]; var c = terrain[face + 2];
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z);
                    var ac = new Vector2(c.X - a.X, c.Z - a.Z);
                    var offset = new Vector2(point.X - a.X, point.Z - a.Z);
                    float cross = ab.X * ac.Y - ab.Y * ac.X;
                    float u = (offset.X * ac.Y - offset.Y * ac.X) / cross;
                    float v = (ab.X * offset.Y - ab.Y * offset.X) / cross;
                    if (u < -0.00001f || v < -0.00001f || u + v > 1.00001f) continue;
                    expected = a.Y + (b.Y - a.Y) * u + (c.Y - a.Y) * v;
                    break;
                }
                Assert.True(float.IsFinite(expected));
                Assert.Equal(expected + ZoneSurfaceLayout.SurfaceLift, point.Y, 5);
            }
    }

    [Fact]
    public void MissingLandAndVerticalCliffFacesDoNotCreateOverlaySkirts()
    {
        Vector3[] wall = [new(0, 0, 0), new(0, 1, 0), new(0, 1, 1)];
        Assert.Empty(ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 0, 0), wall));
        Assert.Empty(ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 0, 0), []));
        Assert.Throws<ArgumentException>(() => ZoneSurfaceLayout.Conform(ZoneSurfaceLayout.Patches(null, 0, 0), [Vector3.Zero]));
    }

    private static Vector3[] Plane(float left, float back, float right, float front, float height) =>
        [new(left, height, back), new(left, height, front), new(right, height, front),
            new(left, height, back), new(right, height, front), new(right, height, back)];

    private static Vector2[] Project(ZoneSurfaceLayout.SurfaceTriangle triangle) =>
        [new(triangle.A.X, triangle.A.Z), new(triangle.B.X, triangle.B.Z), new(triangle.C.X, triangle.C.Z)];

    [Fact]
    public void UnaffectedCellRetainsTheEntireRegularCheckerSurface()
    {
        var patches = ZoneSurfaceLayout.Patches(null, 17, 23);
        Assert.Equal(4, patches.Count);
        Assert.All(patches, patch => Assert.Equal(0.25, Area(patch), 8));
        Assert.Equal(1, patches.Sum(Area), 8);
    }

    [Theory]
    [InlineData(DefaultRoads.TrackName)]
    [InlineData(DefaultRoads.CobbledName)]
    [InlineData(DefaultRoads.KingsRoadName)]
    public void EveryZoneExcludesDiagonalCurvedIntersectionAndBridgeRoadFootprints(string roadName)
    {
        var map = new GameMap(32, 32, new GameContent());
        var type = map.Content.Roads.Get(roadName);
        for (int y = 0; y < 32; y++) map.SetTerrain(16, y, map.Content.Terrains.Get("Water"));
        var layout = new RoadMeshLayout(map,
        [
            Path(type, false, new(4.5f, 4.5f), new(12.5f, 11.5f), new(15.5f, 12.5f),
                new(20.5f, 12.5f), new(23.5f, 14.5f), new(25.5f, 19.5f)),
            Path(type, false, new(8.5f, 16.5f), new(24.5f, 7.5f)),
            Path(type, true, new(7.5f, 21.5f), new(10.5f, 19.5f), new(13.5f, 21.5f), new(10.5f, 24.5f))
        ]);
        Assert.Contains(layout.Segments, segment => segment.Bridge);
        int partiallyCovered = 0, completelyCovered = 0;
        foreach (var zone in Zones.Placeable)
            for (int y = 3; y < 27; y++)
                for (int x = 3; x < 27; x++)
                {
                    map.SetZone(x, y, zone);
                    var patches = ZoneSurfaceLayout.Patches(layout, x, y);
                    double area = patches.Sum(Area);
                    Assert.InRange(area, 0, 1.000001);
                    if (area > 0 && area < 0.99999) partiallyCovered++;
                    if (area == 0) completelyCovered++;
                    foreach (var patch in patches)
                    {
                        Assert.All(patch, point =>
                        {
                            Assert.InRange(point.X, x - 0.00001f, x + 1.00001f);
                            Assert.InRange(point.Y, y - 0.00001f, y + 1.00001f);
                        });
                        // Use every ribbon, not the production cell index, to catch
                        // curved overhangs and ensure the emitted triangle fans cannot overlap.
                        foreach (var road in layout.Segments)
                            for (int i = 1; i < patch.Length - 1; i++)
                                Assert.InRange(IntersectionArea([patch[0], patch[i], patch[i + 1]], road.Road),
                                    0, 0.000002);
                    }
                    Assert.Equal(zone, map.ZoneAt(x, y));
                }
        Assert.True(partiallyCovered > 0);
        Assert.True(completelyCovered > 0);
    }

    [Fact]
    public void RoadFullyInsideCheckerPatchProducesAHoleRatherThanAFilledFan()
    {
        var map = new GameMap(24, 24, new GameContent());
        var original = map.Content.Roads.Default;
        var narrow = new RoadType
        {
            Name = "Geometry fixture",
            Glyphs = original.Glyphs,
            Foreground = original.Foreground,
            Background = original.Background,
            WidthCells = 0.1f
        };
        var layout = new RoadMeshLayout(map, [Path(narrow, false, new(12.2f, 12.25f), new(12.3f, 12.25f))]);
        var patches = ZoneSurfaceLayout.Patches(layout, 12, 12);
        Assert.Equal(0.99, patches.Sum(Area), 6);
        Assert.All(patches, patch =>
        {
            foreach (var road in layout.Segments)
                for (int i = 1; i < patch.Length - 1; i++)
                    Assert.InRange(IntersectionArea([patch[0], patch[i], patch[i + 1]], road.Road), 0, 0.000002);
        });
    }

    [Fact]
    public void ExactRoadEdgeLeavesShoulderAndRestOfTerrainVisible()
    {
        var map = new GameMap(24, 24, new GameContent());
        var layout = new RoadMeshLayout(map,
            [Path(map.Content.Roads.Get(DefaultRoads.KingsRoadName), false, new(4, 10.25f), new(20, 10.25f))]);
        var patches = ZoneSurfaceLayout.Patches(layout, 12, 11);
        Assert.Equal(0.75, patches.Sum(Area), 7);
        Assert.All(patches.SelectMany(p => p), point => Assert.True(point.Y >= 11.25f));
        Assert.True(layout.InCorridor(new(12.5f, 11.3f)));
        Assert.Contains(patches, patch => patch.Any(point => point.Y == 11.25f));
    }

    [Fact]
    public void RebuiltLayoutImmediatelyMasksExistingZonesWithoutChangingGameplay()
    {
        var map = new GameMap(24, 24, new GameContent());
        map.SetZone(12, 12, ZoneType.Residential);
        Assert.Equal(1, ZoneSurfaceLayout.Patches(new RoadMeshLayout(map, []), 12, 12).Sum(Area), 8);
        var layout = new RoadMeshLayout(map,
            [Path(map.Content.Roads.Default, false, new(4, 12.25f), new(20, 12.25f))]);
        Assert.Equal(0.25, ZoneSurfaceLayout.Patches(layout, 12, 12).Sum(Area), 7);
        Assert.Equal(ZoneType.Residential, map.ZoneAt(12, 12));
    }

    [Theory]
    [InlineData(1, 0.84)]
    [InlineData(3, 0.76)]
    public void StraightFootpathRemovesOnlyItsActualWidth(int wear, double expectedArea)
    {
        var paths = new FootpathSurfaceLayout(
            [new FootpathLayout.Stroke([new(10, 12.25f), new(15, 12.25f)], wear)], null);
        var patches = ZoneSurfaceLayout.Patches(null, 12, 12, paths);
        Assert.Equal(expectedArea, patches.Sum(Area), 5);
        AssertNoFootpathOverlap(patches, paths);
        Assert.Equal(4, ZoneSurfaceLayout.Patches(null, 12, 13, paths).Count);
        Assert.Equal(1, ZoneSurfaceLayout.Patches(null, 12, 13, paths).Sum(Area), 8);
    }

    [Fact]
    public void FootpathEnclosedInsideCheckerPatchCutsRibbonAndRoundCapsWithoutFilledHole()
    {
        var paths = new FootpathSurfaceLayout(
            [new FootpathLayout.Stroke([new(12.15f, 12.25f), new(12.35f, 12.25f)], 1)], null);
        var patches = ZoneSurfaceLayout.Patches(null, 12, 12, paths);
        // Twelve-sided caps have combined area 3*r*r; the ribbon remains 0.16 wide.
        Assert.Equal(1 - 0.2 * 0.16 - 3 * 0.08 * 0.08, patches.Sum(Area), 5);
        AssertNoFootpathOverlap(patches, paths);
        Assert.Equal(3, patches.Count(patch => Math.Abs(Area(patch) - 0.25) < 0.000001));
    }

    [Fact]
    public void EveryZoneRetainsItsGameplayLayersOutsideExactTurningAndFlaredFootpathSurface()
    {
        var map = new GameMap(24, 24, new GameContent());
        var roads = new RoadMeshLayout(map,
            [Path(map.Content.Roads.Default, false, new(4, 10.25f), new(20, 10.25f))]);
        var paths = new FootpathSurfaceLayout(
        [
            new FootpathLayout.Stroke([new(12, 10.25f), new(12.5f, 12), new(14, 13), new(14, 15)], 3, true),
            new FootpathLayout.Stroke([new(10, 13.5f), new(16, 13.5f)], 1)
        ], roads);
        Assert.Contains(paths.Surfaces.SelectMany(surface => surface.Polygons), polygon =>
            polygon.Length == 12);
        int partlyCovered = 0;
        foreach (var zone in Zones.Placeable)
            for (int y = 9; y <= 16; y++)
                for (int x = 9; x <= 17; x++)
                {
                    map.SetZone(x, y, zone);
                    int cell = map.Index(x, y);
                    var originalWear = map.PathLayer[cell];
                    var originalRoad = map.RoadLayer[cell];
                    var originalBuilding = map.BuildingLayer[cell];
                    var patches = ZoneSurfaceLayout.Patches(roads, x, y, paths);
                    var roadOnly = ZoneSurfaceLayout.Patches(roads, x, y);
                    double area = patches.Sum(Area);
                    Assert.InRange(area, 0, roadOnly.Sum(Area) + 0.000001);
                    if (area > 0 && area < roadOnly.Sum(Area) - 0.00001) partlyCovered++;
                    AssertNoFootpathOverlap(patches, paths);
                    foreach (var patch in patches)
                    {
                        Assert.All(patch, point =>
                        {
                            Assert.InRange(point.X, x - 0.00001f, x + 1.00001f);
                            Assert.InRange(point.Y, y - 0.00001f, y + 1.00001f);
                        });
                        foreach (var road in roads.Segments)
                            for (int i = 1; i < patch.Length - 1; i++)
                                Assert.InRange(IntersectionArea([patch[0], patch[i], patch[i + 1]], road.Road),
                                    0, 0.000002);
                    }
                    Assert.Equal(zone, map.ZoneAt(x, y));
                    Assert.Equal(originalWear, map.PathLayer[cell]);
                    Assert.Equal(originalRoad, map.RoadLayer[cell]);
                    Assert.Equal(originalBuilding, map.BuildingLayer[cell]);
                }
        Assert.True(partlyCovered > 0);
    }

    [Fact]
    public void FootpathInsideActualRoadDoesNotRemoveAdditionalZoneArea()
    {
        var map = new GameMap(24, 24, new GameContent());
        var roads = new RoadMeshLayout(map,
            [Path(map.Content.Roads.Default, false, new(4, 12.25f), new(20, 12.25f))]);
        var paths = new FootpathSurfaceLayout(
            [new FootpathLayout.Stroke([new(11, 12.25f), new(14, 12.25f)], 3, true, true)], roads);
        Assert.Equal(ZoneSurfaceLayout.Patches(roads, 12, 12).Sum(Area),
            ZoneSurfaceLayout.Patches(roads, 12, 12, paths).Sum(Area), 7);
    }

    private static void AssertNoFootpathOverlap(IReadOnlyList<Vector2[]> patches, FootpathSurfaceLayout paths)
    {
        foreach (var patch in patches)
            foreach (var polygon in paths.Surfaces.SelectMany(surface => surface.Polygons))
                for (int i = 1; i < patch.Length - 1; i++)
                    Assert.InRange(IntersectionArea([patch[0], patch[i], patch[i + 1]], polygon), 0, 0.000002);
    }

    private static RoadPath Path(RoadType type, bool closed, params Vector2[] points) =>
        new(type, true, closed, points.Select(p => (double)p.X * RoadCurves.CellWidth).ToArray(),
            points.Select(p => (double)p.Y * RoadCurves.CellHeight).ToArray());

    private static double Area(Vector2[] polygon) =>
        Area(polygon.Select(point => ((double)point.X, (double)point.Y)).ToList());

    private static double Area(List<(double X, double Y)> polygon)
    {
        if (polygon.Count < 3) return 0;
        double area = 0;
        for (int i = 1; i < polygon.Count - 1; i++)
            area += (polygon[i].X - polygon[0].X) * (polygon[i + 1].Y - polygon[0].Y) -
                (polygon[i].Y - polygon[0].Y) * (polygon[i + 1].X - polygon[0].X);
        return Math.Abs(area) / 2;
    }

    internal static double IntersectionArea(Vector2[] triangle, Vector2[] road)
    {
        var polygon = triangle.Select(point => (X: (double)point.X, Y: (double)point.Y)).ToList();
        for (int i = 0; i < road.Length && polygon.Count > 0; i++)
        {
            var a = road[i]; var b = road[(i + 1) % road.Length];
            double Distance((double X, double Y) point) =>
                ((double)b.X - a.X) * (point.Y - a.Y) - ((double)b.Y - a.Y) * (point.X - a.X);
            var output = new List<(double X, double Y)>();
            var previous = polygon[^1];
            double previousDistance = Distance(previous);
            foreach (var current in polygon)
            {
                double distance = Distance(current);
                if ((distance >= 0) != (previousDistance >= 0))
                {
                    double t = previousDistance / (previousDistance - distance);
                    output.Add((previous.X + (current.X - previous.X) * t,
                        previous.Y + (current.Y - previous.Y) * t));
                }
                if (distance >= 0) output.Add(current);
                previous = current;
                previousDistance = distance;
            }
            polygon = output;
        }
        return Area(polygon);
    }
}
