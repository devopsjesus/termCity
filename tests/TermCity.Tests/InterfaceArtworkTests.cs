using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.World;
using TermCity.Core.Simulation;
using TermCity.Core.Persistence;

namespace TermCity.Tests;

public class InterfaceArtworkTests
{
    [Theory]
    [InlineData(ZoneType.Residential)]
    [InlineData(ZoneType.Commercial)]
    [InlineData(ZoneType.Industrial)]
    public void VacantZoneOverlayUsesTheExistingCellArtwork(ZoneType zone)
    {
        var game = TestCity.Flat();
        game.Map.SetZone(10, 10, zone);
        Assert.Equal(CellRenderer.Render(game, 10, 10), CellRenderer.RenderZone(game, 10, 10));
        Assert.Equal(Zones.Get(zone).EmptyGlyph, CellRenderer.RenderZone(game, 10, 10).Glyph);
        foreach (var overlay in Enum.GetValues<MapOverlay>())
            Assert.Equal(CellRenderer.Render(game, 10, 10, overlay), CellRenderer.RenderZone(game, 10, 10, overlay));
    }

    [Theory]
    [InlineData(ZoneType.Residential)]
    [InlineData(ZoneType.Commercial)]
    [InlineData(ZoneType.Industrial)]
    public void CheckerboardOverlayUsesSolidClustersAndTransparentGaps(ZoneType zone)
    {
        var game = TestCity.Flat();
        game.Map.SetZone(10, 10, zone);
        foreach (var overlay in Enum.GetValues<MapOverlay>())
        {
            var artwork = CellRenderer.RenderZone(game, 10, 10, overlay);
            var pixels = ZoneOverlayArtwork.Pixels(artwork);
            Assert.Equal(ZoneOverlayArtwork.TextureSize * ZoneOverlayArtwork.TextureSize * 4, pixels.Length);
            for (int row = 0; row < ZoneOverlayArtwork.TextureSize; row++)
                for (int column = 0; column < ZoneOverlayArtwork.TextureSize; column++)
                {
                    bool ink = (row / ZoneOverlayArtwork.CheckerSize + column / ZoneOverlayArtwork.CheckerSize) % 2 == 0;
                    var color = ink ? artwork.Foreground : artwork.Background;
                    int index = (row * ZoneOverlayArtwork.TextureSize + column) * 4;
                    Assert.Equal(new byte[] { color.R, color.G, color.B, ink ? (byte)255 : (byte)48 }, pixels[index..(index + 4)]);
                }
            Assert.Equal(ZoneOverlayArtwork.TextureSize * ZoneOverlayArtwork.TextureSize / 2,
                Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] == 255));
        }
        Assert.Equal(1f, ZoneOverlayArtwork.Across / ZoneOverlayArtwork.Down);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void PathsReachTheActualRoadAndFlareSmoothlyAtContact(int directionX, int directionY)
    {
        var map = TestCity.Flat().Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        var roadCenter = new Vector2(15.5f, 15.5f);
        var approach = new Vector2(directionX, directionY);
        var along = new Vector2(-directionY, directionX);
        var road = new RoadPath(map.Content.Roads.Default, true, false,
            [(roadCenter.X - along.X * 3) * 12, (roadCenter.X + along.X * 3) * 12],
            [(roadCenter.Y - along.Y * 3) * 22, (roadCenter.Y + along.Y * 3) * 22]);
        map.SetRoad(15, 15, true);
        int[] journey = Enumerable.Range(0, 4).Select(step => map.Index(15 + directionX * step, 15 + directionY * step)).ToArray();
        map.BuildingPaths = [journey];
        foreach (int cell in journey.Skip(1)) map.PathLayer[cell] = 6;
        var stroke = Assert.Single(FootpathLayout.Strokes(map));
        Assert.True(stroke.StartsAtRoad);
        Assert.False(stroke.EndsAtRoad);
        Assert.Equal(roadCenter, stroke.Points[0]);
        var layout = new RoadMeshLayout(map, [road]);
        var sections = FootpathLayout.RibbonSections(stroke, layout);
        Assert.True(layout.InRoadInterior(sections[0].Center));
        var contact = sections.OrderBy(section => Math.Abs(Vector2.Dot(section.Center - roadCenter, approach) - RoadMeshLayout.HalfWidth)).First();
        Assert.True(Vector2.Distance(contact.Left, contact.Right) > 0.4f);
        Assert.Equal(0.24f, Vector2.Distance(sections[^1].Left, sections[^1].Right), 4);
        for (int index = 1; index < sections.Count; index++)
        {
            Assert.InRange(Vector2.Distance(sections[index - 1].Center, sections[index].Center), 0.00001f, 0.121f);
            Assert.InRange(Math.Abs(Vector2.Distance(sections[index].Left, sections[index].Right) -
                Vector2.Distance(sections[index - 1].Left, sections[index - 1].Right)), 0, 0.09f);
        }
        var reversed = FootpathLayout.RibbonSections(new(stroke.Points.Reverse().ToArray(), stroke.Wear, false, true), layout);
        Assert.Equal(sections.Count, reversed.Count);
        for (int index = 0; index < sections.Count; index++)
            Assert.InRange(Vector2.Distance(sections[index].Center, reversed[^(index + 1)].Center), 0, 0.00001f);
    }

    [Fact]
    public void PathRoadTargetsFollowTheVisibleDiagonalRoadCenterline()
    {
        var map = TestCity.Flat().Map;
        var road = new RoadPath(map.Content.Roads.Default, true, false, [12 * 12, 18 * 12], [12 * 22, 18 * 22]);
        var layout = new RoadMeshLayout(map, [road]);
        var stroke = new FootpathLayout.Stroke([new(17.5f, 15.5f), new(16.5f, 15.5f), new(15.5f, 15.5f)], 6, false, true);
        var sections = FootpathLayout.RibbonSections(stroke, layout);
        Assert.True(layout.InRoadInterior(sections[^1].Center));
        Assert.True(Vector2.Distance(sections[^1].Center, stroke.Points[^1]) > 0.1f);
        Assert.Equal(sections[^1].Center.X, sections[^1].Center.Y, 4);
    }

    [Fact]
    public void ParallelJourneysDoNotInventLadderRungs()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [Enumerable.Range(10, 6).Select(column => map.Index(column, 10)).ToArray(),
            Enumerable.Range(10, 6).Select(column => map.Index(column, 11)).ToArray()];
        foreach (var path in map.BuildingPaths)
            foreach (int cell in path) map.PathLayer[cell] = 6;
        var strokes = FootpathLayout.Strokes(map);
        Assert.Equal(2, strokes.Count);
        Assert.All(strokes, stroke => Assert.All(stroke.Points, point => Assert.Equal(stroke.Points[0].Y, point.Y)));
        Assert.All(strokes, stroke => Assert.Equal(6, stroke.Wear));
    }

    [Fact]
    public void CornersAreSoftenedWithinTheTraversedCells()
    {
        var map = TestCity.Flat().Map;
        int[] route = [map.Index(10, 10), map.Index(11, 10), map.Index(11, 11), map.Index(12, 11)];
        map.BuildingPaths = [route];
        foreach (int cell in route) map.PathLayer[cell] = 3;
        var stroke = Assert.Single(FootpathLayout.Strokes(map));
        Assert.Equal(new Vector2(10.5f, 10.5f), stroke.Points[0]);
        Assert.Equal(new Vector2(12.5f, 11.5f), stroke.Points[^1]);
        Assert.Contains(stroke.Points, point => point.X % 0.5f != 0 && point.Y % 0.5f != 0);
        Assert.All(stroke.Points, point => Assert.Contains(map.Index((int)point.X, (int)point.Y), route));
    }

    [Fact]
    public void UnwornGapsAndAbandonedPathsDoNotCreateInventedConnections()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [Enumerable.Range(10, 5).Select(column => map.Index(column, 10)).ToArray()];
        map.PathLayer[map.Index(10, 10)] = 3;
        map.PathLayer[map.Index(14, 10)] = 3;
        map.PathLayer[map.Index(11, 11)] = 3;
        Assert.Empty(FootpathLayout.Strokes(map));
    }

    [Fact]
    public void PersistentWearWithoutACurrentJourneyRendersAContinuousPathNotDots()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [];
        for (int x = 10; x <= 16; x++) map.PathLayer[map.Index(x, 10)] = 6;
        var stroke = Assert.Single(FootpathLayout.Strokes(map));
        Assert.Equal(new Vector2(10.5f, 10.5f), stroke.Points[0]);
        Assert.Equal(new Vector2(16.5f, 10.5f), stroke.Points[^1]);
        Assert.Equal(7, stroke.Points.Count);
        Assert.All(stroke.Points, point => Assert.Equal(10.5f, point.Y));
    }

    [Fact]
    public void PersistentPathBranchesConnectToBuildingsAndRoadsWithoutBridgingUnwornLand()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [];
        for (int x = 10; x <= 16; x++) map.PathLayer[map.Index(x, 10)] = 6;
        for (int y = 11; y <= 13; y++) map.PathLayer[map.Index(13, y)] = 6;
        map.SetRoad(17, 10, true);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(9, 10, 1, 1));
        var strokes = FootpathLayout.Strokes(map);
        Assert.All(strokes, stroke => Assert.True(stroke.Points.Count >= 2));
        Assert.Contains(strokes, stroke => stroke.EndsAtRoad && stroke.Points[^1] == new Vector2(17.5f, 10.5f));
        Assert.Contains(strokes, stroke => stroke.Points[0] == new Vector2(10, 10.5f) ||
            stroke.Points[^1] == new Vector2(10, 10.5f));
        Assert.Contains(strokes, stroke => stroke.Points[0] == new Vector2(13.5f, 10.5f) ||
            stroke.Points[^1] == new Vector2(13.5f, 10.5f));
        Assert.DoesNotContain(strokes.SelectMany(stroke => stroke.Points), point => point.X < 9.9f);
    }

    [Fact]
    public void IsolatedPersistentWearAndWaterCellsDoNotProduceDotAnomalies()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [];
        map.PathLayer[map.Index(10, 10)] = 6;
        map.PathLayer[map.Index(14, 10)] = 6;
        map.SetTerrain(14, 10, map.Content.Terrains.Get("Water"));
        Assert.Empty(FootpathLayout.Strokes(map));
        Assert.Equal(6, map.PathLayer[map.Index(10, 10)]);
    }

    [Fact]
    public void RealSimWornRoutesKeepTheirAppearanceAfterSaving()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(10, 10, 1, 1));
        map.SetBuildingFootprint(well, new(16, 13, 1, 1));
        for (int day = 0; day < 24; day++) TravelRoutes.WearPaths(map);
        var original = FootpathLayout.Strokes(map);
        Assert.NotEmpty(original);
        Assert.Contains(original, stroke => stroke.Points.Count > 3);
        var restored = FootpathLayout.Strokes(SaveGameStore.Deserialize(SaveGameStore.Serialize(game)).Map);
        Assert.Equal(original.Count, restored.Count);
        for (int index = 0; index < original.Count; index++)
        {
            Assert.Equal(original[index].Points, restored[index].Points);
            Assert.Equal(original[index].StartsAtRoad, restored[index].StartsAtRoad);
            Assert.Equal(original[index].EndsAtRoad, restored[index].EndsAtRoad);
        }
    }

    [Fact]
    public void HouseMaterialsUseStableOpaquePixelClustersWithoutFineLines()
    {
        const int size = HouseAppearance.TextureSize;
        const int cluster = HouseAppearance.TextureClusterSize;
        Assert.True(cluster >= 4);
        Assert.Equal(0, size % cluster);
        foreach (int color in new[] { 0xc9bf9f, 0xbaa98a, 0xd7c9aa, 0x9d947f,
            0xb2a273, 0x9e875e, 0x8d6450, 0x687574, HouseAppearance.StoneColor })
        {
            var pixels = HouseAppearance.MaterialTexture(color);
            Assert.Equal(pixels, HouseAppearance.MaterialTexture(color));
            Assert.Equal(size * size * 4, pixels.Length);
            var tones = new HashSet<byte>();
            for (int row = 0; row < size; row++)
                for (int column = 0; column < size; column++)
                {
                    int pixel = (row * size + column) * 4;
                    int block = (row / cluster * cluster * size + column / cluster * cluster) * 4;
                    Assert.Equal(pixels.AsSpan(block, 4).ToArray(), pixels.AsSpan(pixel, 4).ToArray());
                    Assert.Equal(255, pixels[pixel + 3]);
                    tones.Add(pixels[pixel]);
                }
            Assert.True(tones.Count >= 4, $"Material {color:X6} has only {tones.Count} tones.");
            Assert.True(tones.Max() - tones.Min() >= 24, $"Material {color:X6} needs broad tonal variation.");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => HouseAppearance.MaterialTexture(0));
    }

    [Theory]
    [InlineData(0xb2a273)]
    [InlineData(0x9e875e)]
    [InlineData(0x8d6450)]
    [InlineData(0x687574)]
    [InlineData(HouseAppearance.StoneColor)]
    public void RoofAndMasonryCoursesContainBroadSolidBlocksNotScoredLines(int color)
    {
        const int size = HouseAppearance.TextureSize;
        const int courseHeight = HouseAppearance.TextureClusterSize * 2;
        var pixels = HouseAppearance.MaterialTexture(color);
        for (int row = 0; row < size; row++)
            Assert.Equal(pixels.AsSpan(row / courseHeight * courseHeight * size * 4, size * 4).ToArray(),
                pixels.AsSpan(row * size * 4, size * 4).ToArray());
    }

    [Theory]
    [InlineData(0xb2a273)]
    [InlineData(0x9e875e)]
    [InlineData(0x8d6450)]
    [InlineData(0x687574)]
    public void RoofCoursesAreMirroredAcrossTheRidge(int color)
    {
        const int size = HouseAppearance.TextureSize;
        var pixels = HouseAppearance.MaterialTexture(color);
        for (int row = 0; row < size / 2; row++)
            Assert.Equal(pixels.AsSpan(row * size * 4, size * 4).ToArray(),
                pixels.AsSpan((size - 1 - row) * size * 4, size * 4).ToArray());
    }

    [Fact]
    public void HousesHaveStableVariedSilhouettesAndPalettes()
    {
        var houses = Enumerable.Range(0, 36).Select(index => HouseAppearance.At(index % 6, index / 6, 1)).ToArray();
        Assert.True(houses.Distinct().Count() > 12);
        Assert.Equal(4, houses.Select(house => house.WallColor).Distinct().Count());
        Assert.Equal(4, houses.Select(house => house.RoofColor).Distinct().Count());
        Assert.Contains(houses, house => house.CrosswiseRoof);
        Assert.Contains(houses, house => !house.CrosswiseRoof);
        Assert.Contains(houses, house => house.Wing);
        Assert.Contains(houses, house => house.Chimney);
        var styles = houses.Select(house => house.Style).ToHashSet();
        foreach (var type in new GameContent().Buildings.Where(type => type.Zone == ZoneType.Residential))
            styles.Add(HouseAppearance.At(type, 0, 0).Style);
        Assert.Equal(Enum.GetValues<HouseStyle>().Order(), styles.Order());
        Assert.Equal(HouseAppearance.At(10, 12, 1), HouseAppearance.At(10, 12, 1));
        Assert.True(HouseAppearance.At(10, 12, 3).Height > HouseAppearance.At(10, 12, 1).Height);
        Assert.All(houses, house =>
        {
            Assert.InRange(house.Width, 0.6f, 0.85f);
            Assert.InRange(house.Depth, 0.6f, 0.85f);
        });
    }
}
